using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models;

public sealed class Notification
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string UserId { get; set; } = "";
    public long Sequence { get; set; }
    public string EventKey { get; set; } = "";
    public string Type { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string TargetType { get; set; } = "book";
    public string TargetId { get; set; } = "";
    public string LoanId { get; set; } = "";
    public DateTime? DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public sealed class NotificationPreferences
{
    [BsonId] public string Id { get; set; } = "";
    public bool Reminders { get; set; } = true;
    public bool Email { get; set; }
    public long Sequence { get; set; }
}
