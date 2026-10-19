using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Contracts.Reviews;
namespace WebAppBookLibrary.Services.Reviews;
internal sealed class ReviewContext(MongoDBService mongo,TimeProvider clock)
{
 internal IMongoDatabase Db=>mongo._database;
 internal IMongoCollection<Review> Reviews=>Db.GetCollection<Review>("Reviews");
 internal IMongoCollection<ReviewReport> Reports=>Db.GetCollection<ReviewReport>("ReviewReports");
 internal IMongoCollection<ReviewModerationHistory> History=>Db.GetCollection<ReviewModerationHistory>("ReviewModerationHistory");
 internal IMongoCollection<ReviewRateEvent> Rates=>Db.GetCollection<ReviewRateEvent>("ReviewRateEvents");
 internal DateTime Now=>new BsonDateTime(clock.GetUtcNow().UtcDateTime).ToUniversalTime();
 internal static string? Id(string? id)=>ObjectId.TryParse(id,out var oid)?oid.ToString():null;
 internal static MyReview Own(Review r)=>new(r.Id,r.BookId,r.Rating,r.Text,r.Visibility,r.Version,r.ModerationReason);
 internal async Task<ReviewResult<T>> Transact<T>(string actorId,Func<IClientSessionHandle,User,CancellationToken,Task<ReviewResult<T>>> action,CancellationToken ct)
 {
  var id=Id(actorId);if(id==null)return ReviewResult<T>.Fail(401,"review_account_unavailable");
  try{
   using var session=await Db.Client.StartSessionAsync(cancellationToken:ct);
   return await session.WithTransactionAsync(async(tx,token)=>{
    var actor=await mongo.Users.Find(tx,u=>u.Id==id&&u.IsActive).FirstOrDefaultAsync(token);
    if(actor==null)return ReviewResult<T>.Fail(401,"review_account_unavailable");
    await UserReferenceGuard.TouchAsync(mongo.Users,tx,id,token);
    return await action(tx,actor,token);
   },new TransactionOptions(readConcern:ReadConcern.Snapshot,writeConcern:WriteConcern.WMajority),ct);
  }catch(UserReferenceUnavailableException){return ReviewResult<T>.Fail(401,"review_account_unavailable");}
  catch(MongoException ex) when(ex is MongoWriteException{WriteError.Category:ServerErrorCategory.DuplicateKey} || ex is MongoCommandException{Code:11000}){return ReviewResult<T>.Fail(409,"review_conflict");}
 }
 internal async Task<bool> TouchBook(IClientSessionHandle tx,string bookId,bool active,CancellationToken ct)=>
  (await mongo.Books.UpdateOneAsync(tx,b=>b.Id==bookId&&(!active||b.IsActive),Builders<Book>.Update.Inc(b=>b.ReferenceVersion,1),cancellationToken:ct)).MatchedCount==1;
 internal async Task<bool> Rate(IClientSessionHandle tx,string userId,string operation,int limit,CancellationToken ct)
 {
  var since=Now.AddHours(-1);
  if(await Rates.CountDocumentsAsync(tx,r=>r.UserId==userId&&r.Operation==operation&&r.CreatedAt>since,cancellationToken:ct)>=limit)return false;
  await Rates.InsertOneAsync(tx,new(){UserId=userId,Operation=operation,CreatedAt=Now},cancellationToken:ct);return true;
 }
 internal async Task Save(IClientSessionHandle tx,Review review,CancellationToken ct){review.Version++;review.UpdatedAt=Now;await Reviews.ReplaceOneAsync(tx,r=>r.Id==review.Id,review,cancellationToken:ct);}
}
