using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models;

public sealed class BookChangeRequest
{
    public Book? Original { get; set; }
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string RequestedBy { get; set; } = string.Empty;
    public long BaseVersion { get; set; }
    public Book Proposed { get; set; } = new();
    public string Status { get; set; } = "pending";
    public long Version { get; set; } = 1;
    public string? DecidedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}
