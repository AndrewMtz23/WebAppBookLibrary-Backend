using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;
public sealed class ReviewRateEvent
{
 [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id {get;set;} = ObjectId.GenerateNewId().ToString();
 public string UserId {get;set;} = "";
 public string Operation {get;set;} = "";
 public DateTime CreatedAt {get;set;}
}
