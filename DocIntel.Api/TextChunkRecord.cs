using Microsoft.Extensions.VectorData;

namespace DocIntel.Api;

public class TextChunkRecord
{
    /// <summary>Dimensions produced by the nomic-embed-text model.</summary>
    public const int EmbeddingDimensions = 768;

    [VectorStoreKey]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Links the chunk back to its MongoDB <see cref="DocumentMetadata"/> record.</summary>
    [VectorStoreData(IsIndexed = true)]
    public string DocumentId { get; set; } = string.Empty;

    [VectorStoreData]
    public string FileName { get; set; } = string.Empty;

    [VectorStoreData]
    public int ChunkIndex { get; set; }

    [VectorStoreData]
    public string Text { get; set; } = string.Empty;

    [VectorStoreVector(EmbeddingDimensions, DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}