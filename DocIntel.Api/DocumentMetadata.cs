using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;


namespace DocIntel.Api;

// Model for MongoDB
public class DocumentMetadata
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}