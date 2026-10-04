using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models;

public class CirculationHistoryEntry
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    public string TransitionId { get; set; } = Guid.NewGuid().ToString("N");

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string BookId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string? FromStatus { get; set; }

    public string ToStatus { get; set; } = string.Empty;

    public string? ActorId { get; set; }

    public string? ActorRole { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string? Reason { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = [];
}
