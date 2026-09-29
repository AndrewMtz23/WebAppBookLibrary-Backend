using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;

public sealed class AccountChallenge
{
    [BsonId] public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Email { get; set; } = "";
    public long EmailVersion { get; set; }
    public long CredentialVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public sealed class AccountMailJob
{
    public string? NotificationId { get; set; }
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Stage { get; set; } = "request";
    public string ProtectedPayload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime AvailableAt { get; set; }
    public DateTime LeaseUntil { get; set; }
    public string LeaseId { get; set; } = "";
    public int Attempts { get; set; }
}

public sealed record AccountMailPayload(string Purpose, string Email, string UserId = "", string Token = "", string Link = "", string Title = "", string Body = "");
