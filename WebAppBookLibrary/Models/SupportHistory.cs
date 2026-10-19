using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;
public sealed class SupportHistory
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public string TicketId { get; set; } = "";
    public string? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string FromStatus { get; set; } = "";
    public string ToStatus { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
