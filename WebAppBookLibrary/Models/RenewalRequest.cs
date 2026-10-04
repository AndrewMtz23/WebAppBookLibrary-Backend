using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using WebAppBookLibrary.Domain.Circulation;

namespace WebAppBookLibrary.Models;

public class RenewalRequest
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    public string LoanId { get; set; } = string.Empty;

    [BsonIgnoreIfNull]
    public string? IdempotencyKey { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string BookId { get; set; } = string.Empty;

    public string Status { get; set; } = RenewalRequestStatuses.Pending;

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public DateTime OriginalDueAt { get; set; }
    public long OriginalLoanVersion { get; set; }

    public DateTime RequestedDueAt { get; set; }

    public string? Reason { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string? DecidedByUserId { get; set; }

    public string? DecisionReason { get; set; }

    public long Version { get; set; } = 1;
}
