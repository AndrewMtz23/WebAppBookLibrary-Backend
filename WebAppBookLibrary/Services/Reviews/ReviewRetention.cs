using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Reviews;
public sealed class ReviewRetention(MongoDBService mongo,IOptions<ReviewOptions> options)
{
 public async Task<int> PurgeExpiredAsync(DateTime now,int batchSize,CancellationToken ct)
 {
  var db=mongo._database;var limit=Math.Clamp(batchSize,1,100);var before=now.AddYears(-options.Value.RetentionYears).AddDays(1);var count=0;
  var reports=db.GetCollection<ReviewReport>("ReviewReports");var history=db.GetCollection<ReviewModerationHistory>("ReviewModerationHistory");
  var candidates=await reports.Find(r=>r.Status=="resolved"&&r.ResolvedAt<=before).SortBy(r=>r.ResolvedAt).Limit(limit).ToListAsync(ct);
  foreach(var r in candidates.Where(r=>r.ResolvedAt!.Value.AddYears(options.Value.RetentionYears)<=now))count+=(int)(await reports.DeleteOneAsync(x=>x.Id==r.Id&&x.Version==r.Version&&x.Status=="resolved",ct)).DeletedCount;
  var old=await history.Find(h=>h.CreatedAt<=before).SortBy(h=>h.CreatedAt).Limit(limit).ToListAsync(ct);
  foreach(var h in old.Where(h=>h.CreatedAt.AddYears(options.Value.RetentionYears)<=now))count+=(int)(await history.DeleteOneAsync(x=>x.Id==h.Id,ct)).DeletedCount;
  await db.GetCollection<BsonDocument>("ReviewPurgeEvents").DeleteManyAsync(Builders<BsonDocument>.Filter.Lte("CreatedAt",now.AddDays(-180)),ct);
  return count;
 }
 public static async Task DeleteOwnedAsync(IMongoDatabase db,IClientSessionHandle tx,string userId,CancellationToken ct)
 {
  var ids=await db.GetCollection<Review>("Reviews").Find(tx,r=>r.UserId==userId).Project(r=>r.Id).ToListAsync(ct);
  foreach(var id in ids)await DeleteAsync(db,tx,id,ct);
  await db.GetCollection<ReviewReport>("ReviewReports").DeleteManyAsync(tx,r=>r.UserId==userId,cancellationToken:ct);
  await db.GetCollection<ReviewRateEvent>("ReviewRateEvents").DeleteManyAsync(tx,r=>r.UserId==userId,cancellationToken:ct);
  if(ids.Count>0)await Record(db,tx,"account_deletion",ids.Count,ct);
 }
 public static async Task DeleteBookAsync(IMongoDatabase db,IClientSessionHandle tx,string bookId,CancellationToken ct)
 {
  var ids=await db.GetCollection<Review>("Reviews").Find(tx,r=>r.BookId==bookId).Project(r=>r.Id).ToListAsync(ct);
  foreach(var id in ids)await DeleteAsync(db,tx,id,ct);
  if(ids.Count>0)await Record(db,tx,"book_deletion",ids.Count,ct);
 }
 public static async Task AnonymizeActorAsync(IMongoDatabase db,IClientSessionHandle tx,string actorId,CancellationToken ct)
 {
  await db.GetCollection<Review>("Reviews").UpdateManyAsync(tx,r=>r.ModeratorId==actorId,Builders<Review>.Update.Set(r=>r.ModeratorId,null).Set(r=>r.ModerationReason,"Decisión administrativa").Inc(r=>r.Version,1),cancellationToken:ct);
  await db.GetCollection<ReviewModerationHistory>("ReviewModerationHistory").UpdateManyAsync(tx,r=>r.ActorId==actorId,Builders<ReviewModerationHistory>.Update.Set(r=>r.ActorId,null).Set(r=>r.Reason,"Decisión administrativa"),cancellationToken:ct);
  await db.GetCollection<ReviewReport>("ReviewReports").UpdateManyAsync(tx,r=>r.ResolverId==actorId,Builders<ReviewReport>.Update.Set(r=>r.ResolverId,null).Set(r=>r.ResolutionReason,"Decisión administrativa").Inc(r=>r.Version,1),cancellationToken:ct);
 }
 private static async Task DeleteAsync(IMongoDatabase db,IClientSessionHandle tx,string id,CancellationToken ct)
 {
  await db.GetCollection<Review>("Reviews").DeleteOneAsync(tx,r=>r.Id==id,cancellationToken:ct);
  await db.GetCollection<ReviewReport>("ReviewReports").DeleteManyAsync(tx,r=>r.ReviewId==id,cancellationToken:ct);
  await db.GetCollection<ReviewModerationHistory>("ReviewModerationHistory").DeleteManyAsync(tx,r=>r.ReviewId==id,cancellationToken:ct);
  var notices=db.GetCollection<Notification>("Notifications");var ids=await notices.Find(tx,n=>n.TargetType=="review"&&n.TargetId==id).Project(n=>n.Id).ToListAsync(ct);
  await db.GetCollection<AccountMailJob>("AccountMailOutbox").DeleteManyAsync(tx,Builders<AccountMailJob>.Filter.In(j=>j.NotificationId,ids),cancellationToken:ct);
  await notices.DeleteManyAsync(tx,n=>n.TargetType=="review"&&n.TargetId==id,cancellationToken:ct);
 }
 private static Task Record(IMongoDatabase db,IClientSessionHandle tx,string type,int count,CancellationToken ct)=>db.GetCollection<BsonDocument>("ReviewPurgeEvents").InsertOneAsync(tx,new(){{"Type",type},{"Count",count},{"CreatedAt",DateTime.UtcNow}},cancellationToken:ct);
}
