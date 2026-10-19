using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Contracts.Reviews;
using WebAppBookLibrary.Domain.Reviews;
namespace WebAppBookLibrary.Services.Reviews;
public sealed partial class ReviewQueries(MongoDBService mongo)
{
 private readonly ReviewContext c=new(mongo,TimeProvider.System);
 public async Task<ReviewResult<PublicReviewPage>> PublicAsync(string bookId,ReviewQuery query,CancellationToken ct)
 {
  var id=ReviewContext.Id(bookId);if(id==null)return ReviewResult<PublicReviewPage>.Fail(404,"review_book_unavailable");
  if(!ReviewRules.ValidPage(query.Page,query.PageSize))return ReviewResult<PublicReviewPage>.Fail(400,"review_query_invalid");
  using var session=await c.Db.Client.StartSessionAsync(cancellationToken:ct);
  return await session.WithTransactionAsync(async(tx,token)=>{
   if(!await mongo.Books.Find(tx,b=>b.Id==id&&b.IsActive).AnyAsync(token))return ReviewResult<PublicReviewPage>.Fail(404,"review_book_unavailable");
   var filter=Builders<Review>.Filter.Where(r=>r.BookId==id&&r.Visibility=="visible");
   var total=await c.Reviews.CountDocumentsAsync(tx,filter,cancellationToken:token);
   var stats=await c.Reviews.Aggregate(tx).Match(filter).Group(r=>1,g=>new{Average=g.Average(r=>r.Rating)}).FirstOrDefaultAsync(token);
   var rows=await c.Reviews.Find(tx,filter).SortByDescending(r=>r.CreatedAt).ThenByDescending(r=>r.Id).Skip((query.Page-1)*query.PageSize).Limit(query.PageSize).ToListAsync(token);
   var ids=rows.Select(r=>r.UserId).ToArray();var names=await mongo.Users.Find(tx,u=>ids.Contains(u.Id)).Project(u=>new{u.Id,u.DisplayName}).ToListAsync(token);
   var map=names.ToDictionary(u=>u.Id,u=>string.IsNullOrWhiteSpace(u.DisplayName)?"Lector":u.DisplayName);
   return new ReviewResult<PublicReviewPage>(new(rows.Select(r=>new PublicReview(r.Id,map.GetValueOrDefault(r.UserId,"Lector"),r.Rating!.Value,r.Text,r.CreatedAt,r.UpdatedAt)).ToArray(),query.Page,query.PageSize,total,stats?.Average));
  },new TransactionOptions(readConcern:ReadConcern.Snapshot),ct);
 }
 public Task<ReviewResult<MyReview>> MineAsync(string actorId,string bookId,CancellationToken ct)=>OwnAsync(actorId,bookId,false,ct);
 public Task<ReviewResult<MyReview>> MineByIdAsync(string actorId,string reviewId,CancellationToken ct)=>OwnAsync(actorId,reviewId,true,ct);
 private async Task<ReviewResult<MyReview>> OwnAsync(string actorId,string resourceId,bool byId,CancellationToken ct)
 {
  var actor=ReviewContext.Id(actorId);var id=ReviewContext.Id(resourceId);
  if(actor==null||!await mongo.Users.Find(u=>u.Id==actor&&u.IsActive).AnyAsync(ct))return ReviewResult<MyReview>.Fail(401,"review_account_unavailable");
  if(id==null)return ReviewResult<MyReview>.Fail(400,"review_input_invalid");
  var row=await c.Reviews.Find(r=>r.UserId==actor&&(byId?r.Id==id:r.BookId==id)).FirstOrDefaultAsync(ct);
  return row==null?(byId?ReviewResult<MyReview>.Fail(404,"review_not_found"):new(null)):new(ReviewContext.Own(row));
 }
}
