using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;
public sealed class Review
{
 [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id {get;set;} = ObjectId.GenerateNewId().ToString();
 public string UserId {get;set;} = "";
 public string BookId {get;set;} = "";
 public int? Rating {get;set;}
 public string Text {get;set;} = "";
 public string Visibility {get;set;} = "visible";
 public bool ModerationHold {get;set;}
 public string? ModerationReason {get;set;}
 public string? ModeratorId {get;set;}
 public DateTime? ModeratedAt {get;set;}
 public long ReferenceVersion {get;set;}
 public long Version {get;set;} = 1;
 public DateTime CreatedAt {get;set;}
 public DateTime UpdatedAt {get;set;}
}
