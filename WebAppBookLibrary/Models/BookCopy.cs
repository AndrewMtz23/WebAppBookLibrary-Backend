using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models;

public sealed class BookCopy
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public string InventoryCode { get; set; } = string.Empty;
    public string Status { get; set; } = "maintenance";
    public string? Condition { get; set; }
    public bool InspectionPending { get; set; } = true;
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public long Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? RetirementReason { get; set; }
    [BsonIgnoreIfNull] public string? ActiveAssignmentType { get; set; }
    [BsonIgnoreIfNull] public string? ActiveAssignmentId { get; set; }
    [BsonIgnoreIfNull] public string? CreationKey { get; set; }
    [BsonIgnoreIfNull] public string? CreationFingerprint { get; set; }
    [BsonIgnoreIfNull] public string? ConversionId { get; set; }
}
