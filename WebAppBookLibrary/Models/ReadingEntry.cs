using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models;

public sealed record ReadingEntry
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; init; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string UserId { get; init; } = "";
    [BsonRepresentation(BsonType.ObjectId)] public string BookId { get; init; } = "";
    public string Status { get; init; } = "want_to_read";
    public string ProgressMode { get; init; } = "percent";
    public int ProgressPercent { get; init; }
    public int? CurrentPage { get; init; }
    public int? PageCountSnapshot { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public DateTime? LastProgressAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
}
