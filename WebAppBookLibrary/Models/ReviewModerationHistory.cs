using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;
public sealed class ReviewModerationHistory
{
 [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id {get;set;} = ObjectId.GenerateNewId().ToString();
 public string ReviewId {get;set;} = "";
 public string? ActorId {get;set;}
 public string Action {get;set;} = "";
 public string Reason {get;set;} = "";
 public DateTime CreatedAt {get;set;}
}
