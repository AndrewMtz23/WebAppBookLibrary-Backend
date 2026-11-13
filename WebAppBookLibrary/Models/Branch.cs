using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models;

public sealed class Branch
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = string.Empty;
    public string OrganizationId { get; set; } = "initial";
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;
    public long Version { get; set; } = 1;
    public long ReferenceVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
