using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Reviews;
using WebAppBookLibrary.Domain.Reviews;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Reviews;
public sealed class ReviewModerationStore(MongoDBService mongo,IOptions<ReviewOptions> options,TimeProvider clock)
{
 private readonly ReviewContext c=new(mongo,clock);
 public Task<ReviewResult<string>> ReportAsync(string actorId,string reviewId,ReportReview request,CancellationToken ct)=>c.Transact<string>(actorId,async(tx,actor,token)=>{
  var id=ReviewContext.Id(reviewId);if(id==null||!ReviewRules.ValidReason(request.Reason))return ReviewResult<string>.Fail(400,"review_input_invalid");
  if(actor.Role!="user"||actor.EmailVerifiedAt==null)return ReviewResult<string>.Fail(403,"review_verified_reader_required");
  var review=await c.Reviews.Find(tx,r=>r.Id==id&&r.Visibility=="visible").FirstOrDefaultAsync(token);
  if(review==null||!await c.TouchBook(tx,review.BookId,true,token))return ReviewResult<string>.Fail(404,"review_not_found");
  if(review.UserId==actor.Id)return ReviewResult<string>.Fail(403,"review_own_report");
  var existing=await c.Reports.Find(tx,r=>r.UserId==actor.Id&&r.ReviewId==id).FirstOrDefaultAsync(token);
  if(existing!=null)return new(existing.Id);
  if(!options.Value.AcceptContributions)return ReviewResult<string>.Fail(409,"reviews_intake_disabled");
  if(!await c.Rate(tx,actor.Id,"report",options.Value.ReportsPerHour,token))return ReviewResult<string>.Fail(429,"review_rate_limit",3600);
  // A separate write guard conflicts with removal without changing the author's content version.
  await c.Reviews.UpdateOneAsync(tx,r=>r.Id==id,Builders<Review>.Update.Inc("ReferenceVersion",1L),cancellationToken:token);
  var report=new ReviewReport{UserId=actor.Id,ReviewId=id,Reason=request.Reason.Trim(),CreatedAt=c.Now};
  await c.Reports.InsertOneAsync(tx,report,cancellationToken:token);return new(report.Id);
 },ct);
 public Task<ReviewResult<long>> SetVisibilityAsync(string actorId,string reviewId,SetReviewVisibility request,CancellationToken ct)=>c.Transact<long>(actorId,async(tx,actor,token)=>{
  if(actor.Role!="admin")return ReviewResult<long>.Fail(403,"review_admin_required");
  var id=ReviewContext.Id(reviewId);if(id==null||request.ExpectedVersion<1||!ReviewRules.ValidReason(request.Reason)||request.Visibility is not("visible" or "hidden"))return ReviewResult<long>.Fail(400,"review_input_invalid");
  var r=await c.Reviews.Find(tx,r=>r.Id==id).FirstOrDefaultAsync(token);if(r==null)return ReviewResult<long>.Fail(404,"review_not_found");
  if(r.Version!=request.ExpectedVersion)return ReviewResult<long>.Fail(409,"review_conflict");
  // Serialize references to an inactive author with permanent account deletion too.
  if((await mongo.Users.UpdateOneAsync(tx,u=>u.Id==r.UserId,Builders<User>.Update.Inc(u=>u.ReferenceVersion,1),cancellationToken:token)).MatchedCount==0)return ReviewResult<long>.Fail(404,"review_not_found");
  r.ModerationHold=request.Visibility=="hidden";r.Visibility=r.Visibility=="removed"?"removed":request.Visibility;
  r.ModerationReason=request.Reason.Trim();r.ModeratorId=actor.Id;r.ModeratedAt=c.Now;
  await c.Save(tx,r,token);
  await c.History.InsertOneAsync(tx,new(){ReviewId=id,ActorId=actor.Id,Action=request.Visibility,Reason=r.ModerationReason,CreatedAt=c.Now},cancellationToken:token);
  await ReviewNotifications.AppendAsync(c.Db,tx,r,c.Now,token);return new(r.Version);
 },ct);
 public Task<ReviewResult<long>> ResolveAsync(string actorId,string reportId,ResolveReviewReport request,CancellationToken ct)=>c.Transact<long>(actorId,async(tx,actor,token)=>{
  if(actor.Role!="admin")return ReviewResult<long>.Fail(403,"review_admin_required");
  var id=ReviewContext.Id(reportId);if(id==null||request.ExpectedVersion<1||!ReviewRules.ValidReason(request.Reason)||request.Resolution is not("action_taken" or "dismissed"))return ReviewResult<long>.Fail(400,"review_input_invalid");
  var r=await c.Reports.Find(tx,r=>r.Id==id).FirstOrDefaultAsync(token);if(r==null)return ReviewResult<long>.Fail(404,"review_report_not_found");
  if(r.Version!=request.ExpectedVersion||r.Status!="open")return ReviewResult<long>.Fail(409,"review_conflict");
  if((await c.Reviews.UpdateOneAsync(tx,x=>x.Id==r.ReviewId,Builders<Review>.Update.Inc(x=>x.ReferenceVersion,1),cancellationToken:token)).MatchedCount==0)return ReviewResult<long>.Fail(404,"review_not_found");
  r.Status="resolved";r.Resolution=request.Resolution;r.ResolutionReason=request.Reason.Trim();r.ResolverId=actor.Id;r.ResolvedAt=c.Now;r.Version++;
  await c.Reports.ReplaceOneAsync(tx,x=>x.Id==id,r,cancellationToken:token);return new(r.Version);
 },ct);
}
