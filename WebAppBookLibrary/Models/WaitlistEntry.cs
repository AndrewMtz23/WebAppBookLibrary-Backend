using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using WebAppBookLibrary.Domain.Circulation;

namespace WebAppBookLibrary.Models;

public class WaitlistEntry
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    [BsonIgnoreIfNull]
    public string? IdempotencyKey { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string BookId { get; set; } = string.Empty;

    public string Status { get; set; } = WaitlistStatuses.Queued;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? OfferedAt { get; set; }

    public DateTime? OfferExpiresAt { get; set; }

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? PickupReservationId { get; set; }

    public string? ClosedReason { get; set; }

    public long Version { get; set; } = 1;
}
