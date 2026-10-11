#pragma warning disable SKEXP0010 // OpenAI connector experimental APIs
#pragma warning disable SKEXP0050 // TextChunker is experimental

using System.ClientModel;
using System.Text;
using DocIntel.Api;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using OpenAI;

const string VectorCollectionName = "doc_intel";
const string MetadataCollectionName = "Documents";
const int EmbeddingBatchSize = 32;
const int SearchTop = 3;

// Only plain-text formats are supported until a PDF/DOCX text extractor is added.
string[] allowedExtensions = [".txt", ".md", ".csv", ".json", ".log"];

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var ollamaSection = builder.Configuration.GetSection("Ollama");
var ollamaEndpoint = new Uri(ollamaSection["Endpoint"] ?? "http://localhost:11434/v1");
var chatModel = ollamaSection["ChatModel"] ?? "llama3.1";
var embeddingModel = ollamaSection["EmbeddingModel"] ?? "nomic-embed-text";

// 1. Configure the OpenAI Connector for Chat (pointing to local Ollama)
builder.Services.AddOpenAIChatCompletion(
    modelId: chatModel,
    apiKey: "none", // Ollama doesn't need a real key, but the connector requires a string
    endpoint: ollamaEndpoint // Notice the /v1, which mimics OpenAI's routing
);

// 2. Configure the OpenAI Connector for Embeddings (pointing to local Ollama)
// Registers IEmbeddingGenerator<string, Embedding<float>> (Microsoft.Extensions.AI).
builder.Services.AddOpenAIEmbeddingGenerator(
    modelId: embeddingModel,
    openAIClient: new OpenAIClient(
        new ApiKeyCredential("none"),
        new OpenAIClientOptions { Endpoint = ollamaEndpoint })
);

// AI & Vector Store (Persistent via ChromaDB)
var chromaEndpoint = new Uri(builder.Configuration["Chroma:Endpoint"] ?? "http://localhost:8000");
builder.Services.AddHttpClient<VectorStore, ChromaVectorStore>(client =>
{
    var baseUri = chromaEndpoint.ToString().TrimEnd('/');
    client.BaseAddress = new Uri($"{baseUri}/api/v2/tenants/default_tenant/databases/default_database/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// MongoDB Client
builder.Services.AddSingleton<IMongoClient>(_ =>
    new MongoClient(builder.Configuration.GetConnectionString("MongoDb") ?? "mongodb://localhost:27017"));

// Health checks
// "ready" checks probe dependencies; the liveness endpoint runs none of them.
builder.Services.AddHttpClient<OllamaHealthCheck>(client =>
    client.BaseAddress = new Uri(ollamaEndpoint.ToString().TrimEnd('/') + "/"));

builder.Services.AddHttpClient<ChromaHealthCheck>(client =>
    client.BaseAddress = new Uri(chromaEndpoint.ToString().TrimEnd('/') + "/"));

var healthCheckTimeout = TimeSpan.FromSeconds(5);
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"], timeout: healthCheckTimeout)
    .AddCheck<OllamaHealthCheck>("ollama", tags: ["ready"], timeout: healthCheckTimeout)
    .AddCheck<ChromaHealthCheck>("chromadb", tags: ["ready"], timeout: healthCheckTimeout)
    .AddTypeActivatedCheck<VectorStoreHealthCheck>("vector-store", failureStatus: null, tags: ["ready"], timeout: healthCheckTimeout, args: VectorCollectionName);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseHttpsRedirection();

#region health

// Readiness: checks MongoDB, Ollama and the vector store. Returns 503 if any dependency is unhealthy.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteJson
});

// Liveness: the process is up and serving requests. Runs no dependency checks.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthCheckResponseWriter.WriteJson
});

#endregion


#region doc-intel

app.MapPost("/api/ingest", async (
    IFormFile file,
    IConfiguration config,
    VectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IMongoClient mongoClient,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    // 0. Validate input
    var maxFileSize = config.GetValue<long?>("Ingestion:MaxFileSizeBytes") ?? 10 * 1024 * 1024;
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { Message = "A non-empty file is required." });
    if (file.Length > maxFileSize)
        return Results.BadRequest(new { Message = $"File exceeds the {maxFileSize} byte limit." });

    // Never trust the client-supplied name for the path on disk (path traversal).
    var originalFileName = Path.GetFileName(file.FileName);
    var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
    if (!allowedExtensions.Contains(extension))
        return Results.BadRequest(new { Message = $"Unsupported file type '{extension}'. Allowed: {string.Join(", ", allowedExtensions)}" });

    // 1. Receive & Save File
    var uploadFolder = Path.GetFullPath(config["Ingestion:UploadFolder"] ?? "uploads");
    Directory.CreateDirectory(uploadFolder);
    var filePath = Path.Combine(uploadFolder, $"{Guid.NewGuid():N}{extension}");

    await using (var fileStream = File.Create(filePath))
    {
        await file.CopyToAsync(fileStream, ct);
    }

    var collection = vectorStore.GetCollection<string, TextChunkRecord>(VectorCollectionName);
    var upsertedIds = new List<string>();

    try
    {
        var textContent = await File.ReadAllTextAsync(filePath, ct);
        if (string.IsNullOrWhiteSpace(textContent))
        {
            File.Delete(filePath);
            return Results.BadRequest(new { Message = "The file contains no text." });
        }

        // 2. Chunk Appropriately (Semantic Kernel TextChunker)
        // First split into lines to respect natural boundaries, then into overlapping paragraphs.
        var lines = TextChunker.SplitPlainTextLines(textContent, maxTokensPerLine: 100);
        var paragraphs = TextChunker.SplitPlainTextParagraphs(lines, maxTokensPerParagraph: 300, overlapTokens: 30);

        // Generate the Mongo id up front so every chunk can reference its source document.
        var documentId = ObjectId.GenerateNewId().ToString();

        // 3. Initialize Vector Store Collection
        await collection.EnsureCollectionExistsAsync(ct);

        // 4. Generate Embeddings (in batches) & Save to Vector Store
        var chunkIndex = 0;
        foreach (var batch in paragraphs.Chunk(EmbeddingBatchSize))
        {
            var embeddings = await embeddingGenerator.GenerateAsync(batch, cancellationToken: ct);

            var records = batch.Select((text, i) => new TextChunkRecord
            {
                DocumentId = documentId,
                FileName = originalFileName,
                ChunkIndex = chunkIndex + i,
                Text = text,
                Embedding = embeddings[i].Vector
            }).ToList();

            await collection.UpsertAsync(records, ct);
            upsertedIds.AddRange(records.Select(r => r.Id));
            chunkIndex += batch.Length;
        }

        // 5. Store Metadata in MongoDB
        var documents = mongoClient
            .GetDatabase(config["MongoDb:DatabaseName"] ?? "DocIntel")
            .GetCollection<DocumentMetadata>(MetadataCollectionName);

        await documents.InsertOneAsync(new DocumentMetadata
        {
            Id = documentId,
            FileName = originalFileName,
            FilePath = filePath,
            ChunkCount = paragraphs.Count
        }, cancellationToken: ct);

        return Results.Ok(new
        {
            DocumentId = documentId,
            ChunkCount = paragraphs.Count,
            Message = $"Successfully vectorized {paragraphs.Count} chunks."
        });
    }
    catch (Exception ex)
    {
        // Roll back partial work so the vector store, MongoDB and disk stay consistent.
        logger.LogError(ex, "Ingestion failed for {FileName}", originalFileName);
        if (upsertedIds.Count > 0)
            await collection.DeleteAsync(upsertedIds, CancellationToken.None);
        if (File.Exists(filePath))
            File.Delete(filePath);
        throw;
    }
})
.DisableAntiforgery(); // Required for IFormFile in Minimal APIs without a frontend yet


app.MapGet("/api/documents", async (
    IMongoClient mongoClient,
    IConfiguration config,
    CancellationToken ct) =>
{
    var documents = mongoClient
        .GetDatabase(config["MongoDb:DatabaseName"] ?? "DocIntel")
        .GetCollection<DocumentMetadata>(MetadataCollectionName);

    var list = await documents.Find(_ => true)
        .SortByDescending(d => d.UploadedAt)
        .ToListAsync(ct);

    return Results.Ok(list);
});


app.MapPost("/api/ask", async (
    AskRequest request,
    VectorStore vectorStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IChatCompletionService chatService,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { Message = "A question is required." });

    var collection = vectorStore.GetCollection<string, TextChunkRecord>(VectorCollectionName);
    if (!await collection.CollectionExistsAsync(ct))
        return Results.Ok(new AskResponse("No documents have been ingested yet.", []));

    // 1. Embed the User's Query
    var queryEmbedding = await embeddingGenerator.GenerateVectorAsync(request.Question, cancellationToken: ct);

    // 2. Search the Vector Store for the most relevant chunks
    // 3. Extract Text from Search Results
    var contextText = new StringBuilder();
    var sources = new List<SourceReference>();
    await foreach (var result in collection.SearchAsync(queryEmbedding, SearchTop, cancellationToken: ct))
    {
        contextText
            .AppendLine($"[Source: {result.Record.FileName}, chunk {result.Record.ChunkIndex}]")
            .AppendLine(result.Record.Text)
            .AppendLine("---");
        sources.Add(new SourceReference(result.Record.DocumentId, result.Record.FileName, result.Record.ChunkIndex, result.Score));
    }

    if (sources.Count == 0)
        return Results.Ok(new AskResponse("I don't know.", sources));

    // 4. Construct Prompt & Call LLM
    var chatHistory = new ChatHistory();
    chatHistory.AddSystemMessage(
        "You are a helpful document intelligence assistant. " +
        "Use only the provided context to answer the question. " +
        "If the answer is not in the context, say 'I don't know'.");
    chatHistory.AddUserMessage($"Context:\n{contextText}\nQuestion: {request.Question}");

    var response = await chatService.GetChatMessageContentAsync(chatHistory, cancellationToken: ct);

    return Results.Ok(new AskResponse(response.Content ?? string.Empty, sources));
});

#endregion

app.Run();

record AskRequest(string Question);

record SourceReference(string DocumentId, string FileName, int ChunkIndex, double? Score);

record AskResponse(string Answer, IReadOnlyList<SourceReference> Sources);
