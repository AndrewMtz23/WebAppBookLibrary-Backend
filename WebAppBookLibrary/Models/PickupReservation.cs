using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using WebAppBookLibrary.Domain.Circulation;

namespace WebAppBookLibrary.Models;

public class PickupReservation
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    [BsonIgnoreIfNull]
    public string? IdempotencyKey { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string BookId { get; set; } = string.Empty;

    public string Status { get; set; } = PickupReservationStatuses.Ready;

    public DateTime ReadyAt { get; set; } = DateTime.UtcNow;

    public DateTime PickupExpiresAt { get; set; }

    public DateTime? CollectedAt { get; set; }

    public DateTime? CancelledAt { get; set; }

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? WaitlistEntryId { get; set; }

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    public string? LoanId { get; set; }

    public string? CancelledReason { get; set; }

    public CirculationPolicySnapshot PolicySnapshot { get; set; } = new();

    public long Version { get; set; } = 1;
}
