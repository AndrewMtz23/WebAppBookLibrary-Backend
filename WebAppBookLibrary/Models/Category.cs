using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;
public sealed class Category
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public long Version { get; set; } = 1;
    public long ReferenceVersion { get; set; }
    public List<string> Aliases { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
