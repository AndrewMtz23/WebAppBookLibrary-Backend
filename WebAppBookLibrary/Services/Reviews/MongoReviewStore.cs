using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Reviews;
using WebAppBookLibrary.Domain.Reviews;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Reviews;
public sealed class MongoReviewStore(MongoDBService mongo,IOptions<ReviewOptions> options,TimeProvider clock)
{
 private readonly ReviewContext c=new(mongo,clock);
 public Task<ReviewResult<MyReview>> SaveAsync(string actorId,string bookId,SaveReview request,CancellationToken ct)=>c.Transact<MyReview>(actorId,async(tx,actor,token)=>{
  var id=ReviewContext.Id(bookId);
  if(id==null||!ReviewRules.ValidRating(request.Rating)||request.Text==null||request.Text.Length>2000||request.ExpectedVersion<0)return ReviewResult<MyReview>.Fail(400,"review_input_invalid");
  if(actor.Role!="user"||actor.EmailVerifiedAt==null)return ReviewResult<MyReview>.Fail(403,"review_verified_reader_required");
  if(string.IsNullOrWhiteSpace(actor.DisplayName))return ReviewResult<MyReview>.Fail(400,"review_display_name_required");
  if(!options.Value.AcceptContributions)return ReviewResult<MyReview>.Fail(409,"reviews_intake_disabled");
  if(!await c.TouchBook(tx,id,true,token))return ReviewResult<MyReview>.Fail(404,"review_book_unavailable");
  var review=await c.Reviews.Find(tx,r=>r.UserId==actor.Id&&r.BookId==id).FirstOrDefaultAsync(token);
  if((review?.Version??0)!=request.ExpectedVersion)return ReviewResult<MyReview>.Fail(409,"review_conflict");
  if(!await c.Rate(tx,actor.Id,"publish",options.Value.PublicationsPerHour,token))return ReviewResult<MyReview>.Fail(429,"review_rate_limit",3600);
  var fresh=review==null;
  review??=new Review{UserId=actor.Id,BookId=id,CreatedAt=c.Now,UpdatedAt=c.Now};
  review.Rating=request.Rating;review.Text=request.Text.Trim();review.Visibility=ReviewRules.VisibilityAfterSave(review.ModerationHold);
  if(fresh)await c.Reviews.InsertOneAsync(tx,review,cancellationToken:token);else await c.Save(tx,review,token);
  return new(ReviewContext.Own(review));
 },ct);
 public Task<ReviewResult<long>> RemoveAsync(string actorId,string bookId,RemoveReview request,CancellationToken ct)=>c.Transact<long>(actorId,async(tx,actor,token)=>{
  var id=ReviewContext.Id(bookId);if(id==null||request.ExpectedVersion<1)return ReviewResult<long>.Fail(400,"review_input_invalid");
  var review=await c.Reviews.Find(tx,r=>r.UserId==actor.Id&&r.BookId==id).FirstOrDefaultAsync(token);
  if(review==null)return ReviewResult<long>.Fail(404,"review_not_found");
  if(review.Version!=request.ExpectedVersion)return ReviewResult<long>.Fail(409,"review_conflict");
  review.Visibility="removed";review.Text="";review.Rating=null;await c.Save(tx,review,token);return new(review.Version);
 },ct);
}
