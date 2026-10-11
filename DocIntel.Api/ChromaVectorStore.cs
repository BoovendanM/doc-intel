using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.VectorData;

namespace DocIntel.Api;

public sealed class ChromaVectorStore(HttpClient httpClient) : VectorStore
{
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name,
        VectorStoreCollectionDefinition? definition = null)
    {
        if (typeof(TKey) != typeof(string) || typeof(TRecord) != typeof(TextChunkRecord))
        {
            throw new NotSupportedException($"ChromaVectorStore only supports string keys and TextChunkRecord models. Requested: {typeof(TKey).Name}, {typeof(TRecord).Name}");
        }

        return (VectorStoreCollection<TKey, TRecord>)(object)new ChromaVectorCollection(name, httpClient);
    }

    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        var col = new ChromaVectorCollection(name, httpClient);
        return col.CollectionExistsAsync(cancellationToken);
    }

    public override async IAsyncEnumerable<string> ListCollectionNamesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var collections = await httpClient.GetFromJsonAsync<List<ChromaCollectionResponse>>("collections", cancellationToken);
        if (collections != null)
        {
            foreach (var c in collections)
            {
                yield return c.Name;
            }
        }
    }

    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        var col = new ChromaVectorCollection(name, httpClient);
        return col.EnsureCollectionDeletedAsync(cancellationToken);
    }

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition? definition = null) =>
        throw new NotSupportedException("Dynamic collections are not supported.");

    public override object? GetService(Type serviceType, object? serviceKey = null) => null;
}

public sealed class ChromaVectorCollection(string name, HttpClient httpClient) : VectorStoreCollection<string, TextChunkRecord>
{
    private string? _collectionId;

    public override string Name => name;

    public override async Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await httpClient.GetAsync($"collections/{Uri.EscapeDataString(name)}", cancellationToken);
            if (!res.IsSuccessStatusCode) return false;

            var col = await res.Content.ReadFromJsonAsync<ChromaCollectionResponse>(cancellationToken);
            if (col?.Id != null)
            {
                _collectionId = col.Id;

                // Also check if any items are stored in this collection
                var countRes = await httpClient.GetAsync($"collections/{col.Id}/count", cancellationToken);
                if (countRes.IsSuccessStatusCode)
                {
                    var count = await countRes.Content.ReadFromJsonAsync<int>(cancellationToken);
                    return count > 0;
                }

                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public override async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        await GetOrCreateCollectionIdAsync(cancellationToken);
    }

    public override async Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        await httpClient.DeleteAsync($"collections/{Uri.EscapeDataString(name)}", cancellationToken);
        _collectionId = null;
    }

    public override async Task UpsertAsync(IEnumerable<TextChunkRecord> records, CancellationToken cancellationToken = default)
    {
        var collectionId = await GetOrCreateCollectionIdAsync(cancellationToken);
        var recordList = records.ToList();
        if (recordList.Count == 0) return;

        var payload = new
        {
            ids = recordList.Select(r => r.Id).ToArray(),
            embeddings = recordList.Select(r => r.Embedding.ToArray()).ToArray(),
            documents = recordList.Select(r => r.Text).ToArray(),
            metadatas = recordList.Select(r => new Dictionary<string, object>
            {
                ["documentId"] = r.DocumentId,
                ["fileName"] = r.FileName,
                ["chunkIndex"] = r.ChunkIndex
            }).ToArray()
        };

        var response = await httpClient.PostAsJsonAsync($"collections/{collectionId}/upsert", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public override Task UpsertAsync(TextChunkRecord record, CancellationToken cancellationToken = default) =>
        UpsertAsync([record], cancellationToken);

    public override async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var collectionId = await GetOrCreateCollectionIdAsync(cancellationToken);
        var payload = new { ids = new[] { id } };
        await httpClient.PostAsJsonAsync($"collections/{collectionId}/delete", payload, cancellationToken);
    }

    public override async Task DeleteAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        var collectionId = await GetOrCreateCollectionIdAsync(cancellationToken);
        var payload = new { ids = idList.ToArray() };
        await httpClient.PostAsJsonAsync($"collections/{collectionId}/delete", payload, cancellationToken);
    }

    public override async IAsyncEnumerable<VectorSearchResult<TextChunkRecord>> SearchAsync<TInput>(
        TInput vector,
        int top,
        VectorSearchOptions<TextChunkRecord>? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var collectionId = await GetOrCreateCollectionIdAsync(cancellationToken);

        float[] vectorArray = vector switch
        {
            ReadOnlyMemory<float> rom => rom.ToArray(),
            float[] arr => arr,
            _ => throw new NotSupportedException($"Vector type {typeof(TInput).Name} is not supported.")
        };

        var queryPayload = new
        {
            query_embeddings = new[] { vectorArray },
            n_results = top
        };

        var response = await httpClient.PostAsJsonAsync($"collections/{collectionId}/query", queryPayload, cancellationToken);
        response.EnsureSuccessStatusCode();

        var queryResult = await response.Content.ReadFromJsonAsync<ChromaQueryResult>(cancellationToken);
        if (queryResult?.Ids == null || queryResult.Ids.Count == 0 || queryResult.Ids[0].Count == 0)
        {
            yield break;
        }

        var ids = queryResult.Ids[0];
        var docs = queryResult.Documents != null && queryResult.Documents.Count > 0 ? queryResult.Documents[0] : null;
        var metadatas = queryResult.Metadatas != null && queryResult.Metadatas.Count > 0 ? queryResult.Metadatas[0] : null;
        var distances = queryResult.Distances != null && queryResult.Distances.Count > 0 ? queryResult.Distances[0] : null;

        for (int i = 0; i < ids.Count; i++)
        {
            var meta = metadatas != null && i < metadatas.Count ? metadatas[i] : null;
            var doc = docs != null && i < docs.Count ? docs[i] : string.Empty;
            var dist = distances != null && i < distances.Count ? distances[i] : 0f;

            var record = new TextChunkRecord
            {
                Id = ids[i],
                Text = doc ?? string.Empty,
                DocumentId = meta?.GetValueOrDefault("documentId")?.ToString() ?? string.Empty,
                FileName = meta?.GetValueOrDefault("fileName")?.ToString() ?? string.Empty,
                ChunkIndex = meta != null && meta.TryGetValue("chunkIndex", out var ci) && ci is JsonElement je && je.TryGetInt32(out var idx) ? idx : 0
            };

            // Cosine similarity score = 1.0 - distance
            double? score = 1.0 - dist;

            yield return new VectorSearchResult<TextChunkRecord>(record, score);
        }
    }

    public override Task<TextChunkRecord?> GetAsync(string key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public override async IAsyncEnumerable<TextChunkRecord> GetAsync(
        Expression<Func<TextChunkRecord, bool>> filter,
        int top,
        FilteredRecordRetrievalOptions<TextChunkRecord>? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) => null;

    private async Task<string> GetOrCreateCollectionIdAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_collectionId)) return _collectionId;

        // Try getting existing
        var getRes = await httpClient.GetAsync($"collections/{Uri.EscapeDataString(name)}", cancellationToken);
        if (getRes.IsSuccessStatusCode)
        {
            var existing = await getRes.Content.ReadFromJsonAsync<ChromaCollectionResponse>(cancellationToken);
            if (existing?.Id != null)
            {
                _collectionId = existing.Id;
                return existing.Id;
            }
        }

        // Create new
        var createPayload = new { name };
        var createRes = await httpClient.PostAsJsonAsync("collections", createPayload, cancellationToken);
        createRes.EnsureSuccessStatusCode();

        var created = await createRes.Content.ReadFromJsonAsync<ChromaCollectionResponse>(cancellationToken);
        _collectionId = created?.Id ?? throw new InvalidOperationException("Failed to obtain collection ID from Chroma.");
        return _collectionId;
    }
}

public sealed class ChromaCollectionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class ChromaQueryResult
{
    [JsonPropertyName("ids")]
    public List<List<string>>? Ids { get; set; }

    [JsonPropertyName("documents")]
    public List<List<string?>>? Documents { get; set; }

    [JsonPropertyName("metadatas")]
    public List<List<Dictionary<string, object>?>>? Metadatas { get; set; }

    [JsonPropertyName("distances")]
    public List<List<float>>? Distances { get; set; }
}
