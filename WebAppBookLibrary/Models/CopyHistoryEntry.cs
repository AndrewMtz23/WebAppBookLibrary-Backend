using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models;

public sealed class CopyHistoryEntry
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;
    public string CopyId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = string.Empty;
    public string? ActorId { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    [BsonIgnoreIfNull] public string? OperationKey { get; set; }
    [BsonIgnoreIfNull] public string? OperationFingerprint { get; set; }
}
