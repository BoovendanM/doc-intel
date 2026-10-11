using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.VectorData;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DocIntel.Api;

/// <summary>Pings MongoDB with the lightweight <c>{ ping: 1 }</c> admin command.</summary>
public sealed class MongoHealthCheck(IMongoClient mongoClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await mongoClient.GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);

            return HealthCheckResult.Healthy("MongoDB is reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "MongoDB is unreachable.", ex);
        }
    }
}

/// <summary>
/// Calls Ollama's OpenAI-compatible <c>GET /v1/models</c> and verifies the configured
/// chat and embedding models have been pulled.
/// </summary>
public sealed class OllamaHealthCheck(HttpClient httpClient, IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("models", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new HealthCheckResult(context.Registration.FailureStatus, $"Ollama returned HTTP {(int)response.StatusCode}.");

            var payload = await response.Content.ReadFromJsonAsync<ModelList>(cancellationToken);
            var available = payload?.Data?.Select(m => Normalize(m.Id)).ToHashSet() ?? [];

            string[] required =
            [
                config["Ollama:ChatModel"] ?? "llama3.1",
                config["Ollama:EmbeddingModel"] ?? "nomic-embed-text"
            ];
            var missing = required.Where(m => !available.Contains(Normalize(m))).ToArray();

            var data = new Dictionary<string, object>
            {
                ["requiredModels"] = required,
                ["missingModels"] = missing
            };

            return missing.Length == 0
                ? HealthCheckResult.Healthy("Ollama is reachable and all models are available.", data)
                : new HealthCheckResult(context.Registration.FailureStatus,
                    $"Ollama is reachable but models are missing: {string.Join(", ", missing)}. Run 'ollama pull <model>'.",
                    data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Ollama is unreachable.", ex);
        }
    }

    // Ollama reports "llama3.1:latest" for a model pulled as "llama3.1".
    private static string Normalize(string model) => model.Contains(':') ? model : $"{model}:latest";

    private sealed record ModelList(List<ModelInfo>? Data);

    private sealed record ModelInfo(string Id);
}

/// <summary>
/// Calls ChromaDB's heartbeat endpoint to verify the vector database service is reachable.
/// </summary>
public sealed class ChromaHealthCheck(HttpClient httpClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("api/v2/heartbeat", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("ChromaDB is reachable.")
                : new HealthCheckResult(context.Registration.FailureStatus, $"ChromaDB returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "ChromaDB is unreachable.", ex);
        }
    }
}

/// <summary>
/// Reports whether the vector collection exists in ChromaDB.
/// </summary>
public sealed class VectorStoreHealthCheck(VectorStore vectorStore, string collectionName) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var exists = await vectorStore
            .GetCollection<string, TextChunkRecord>(collectionName)
            .CollectionExistsAsync(cancellationToken);

        return HealthCheckResult.Healthy(
            exists ? "Vector collection is available in ChromaDB." : "Vector collection is empty - no documents ingested yet.",
            new Dictionary<string, object> { ["collection"] = collectionName, ["collectionExists"] = exists });
    }
}

public static class HealthCheckResponseWriter
{
    /// <summary>Writes a JSON body with the overall status and per-check details.</summary>
    public static Task WriteJson(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            Status = report.Status.ToString(),
            TotalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            Checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    Status = e.Value.Status.ToString(),
                    e.Value.Description,
                    DurationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                    Error = e.Value.Exception?.Message,
                    Data = e.Value.Data.Count > 0 ? e.Value.Data : null
                })
        });
}

