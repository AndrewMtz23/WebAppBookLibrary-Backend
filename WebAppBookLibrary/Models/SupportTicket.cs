using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;
public sealed class SupportTicket
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string RequesterId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Description { get; set; } = "";
    public string? BookId { get; set; }
    public string? LoanId { get; set; }
    public string Status { get; set; } = "open";
    public string? AssigneeId { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public long Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
}
