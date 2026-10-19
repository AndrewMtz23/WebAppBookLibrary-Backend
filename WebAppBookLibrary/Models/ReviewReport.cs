using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
namespace WebAppBookLibrary.Models;
public sealed class ReviewReport
{
 [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id {get;set;} = ObjectId.GenerateNewId().ToString();
 public string UserId {get;set;} = "";
 public string ReviewId {get;set;} = "";
 public string Reason {get;set;} = "";
 public string Status {get;set;} = "open";
 public string? Resolution {get;set;}
 public string? ResolutionReason {get;set;}
 public string? ResolverId {get;set;}
 public long Version {get;set;} = 1;
 public DateTime CreatedAt {get;set;}
 public DateTime? ResolvedAt {get;set;}
}
