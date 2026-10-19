using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;
public sealed class SupportReply
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string TicketId { get; set; } = "";
    public string? AuthorId { get; set; }
    public bool IsStaff { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
