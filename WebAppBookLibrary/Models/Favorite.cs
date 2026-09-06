using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WebAppBookLibrary.Models;

public sealed class Favorite
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;
    [BsonRepresentation(BsonType.ObjectId)]
    public string UserId { get; set; } = string.Empty;
    [BsonRepresentation(BsonType.ObjectId)]
    public string BookId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
