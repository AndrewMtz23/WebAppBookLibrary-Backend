using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Reviews;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Domain.Reviews;
using WebAppBookLibrary.Models;
namespace WebAppBookLibrary.Services.Reviews;
public sealed partial class ReviewQueries
{
 private async Task<bool> IsAdmin(string actorId,CancellationToken ct)=>ReviewContext.Id(actorId)!=null&&await mongo.Users.Find(u=>u.Id==actorId&&u.IsActive&&u.Role=="admin").AnyAsync(ct);
 public async Task<ReviewResult<PagedResult<MyReview>>> AdminListAsync(string actorId,ReviewQuery query,CancellationToken ct)
 {
  if(!await IsAdmin(actorId,ct))return ReviewResult<PagedResult<MyReview>>.Fail(403,"review_admin_required");
  if(!ReviewRules.ValidPage(query.Page,query.PageSize)||query.Visibility!=null&&query.Visibility is not("visible" or "hidden" or "removed")||query.BookId!=null&&ReviewContext.Id(query.BookId)==null)return ReviewResult<PagedResult<MyReview>>.Fail(400,"review_query_invalid");
  var book=ReviewContext.Id(query.BookId);var f=Builders<Review>.Filter.Where(r=>(query.Visibility==null||r.Visibility==query.Visibility)&&(book==null||r.BookId==book));
  var total=await c.Reviews.CountDocumentsAsync(f,cancellationToken:ct);var rows=await c.Reviews.Find(f).SortByDescending(r=>r.UpdatedAt).ThenByDescending(r=>r.Id).Skip((query.Page-1)*query.PageSize).Limit(query.PageSize).ToListAsync(ct);
  return new(new(rows.Select(ReviewContext.Own).ToArray(),query.Page,query.PageSize,total));
 }
 public async Task<ReviewResult<AdminReviewDetail>> AdminDetailAsync(string actorId,string reviewId,int page,int pageSize,CancellationToken ct)
 {
  if(!await IsAdmin(actorId,ct))return ReviewResult<AdminReviewDetail>.Fail(403,"review_admin_required");
  if(!ReviewRules.ValidPage(page,pageSize))return ReviewResult<AdminReviewDetail>.Fail(400,"review_query_invalid");
  var id=ReviewContext.Id(reviewId);var r=id==null?null:await c.Reviews.Find(r=>r.Id==id).FirstOrDefaultAsync(ct);if(r==null)return ReviewResult<AdminReviewDetail>.Fail(404,"review_not_found");
  var total=await c.History.CountDocumentsAsync(h=>h.ReviewId==id,cancellationToken:ct);
  var rows=await c.History.Find(h=>h.ReviewId==id).SortByDescending(h=>h.CreatedAt).ThenByDescending(h=>h.Id).Skip((page-1)*pageSize).Limit(pageSize).ToListAsync(ct);
  return new(new(ReviewContext.Own(r),new(rows.Select(h=>new ReviewHistoryEntry(h.Id,h.Action,h.Reason,h.CreatedAt)).ToArray(),page,pageSize,total)));
 }
 public async Task<ReviewResult<PagedResult<ReviewReportResponse>>> ReportsAsync(string actorId,ReviewReportQuery query,CancellationToken ct)
 {
  if(!await IsAdmin(actorId,ct))return ReviewResult<PagedResult<ReviewReportResponse>>.Fail(403,"review_admin_required");
  if(!ReviewRules.ValidPage(query.Page,query.PageSize)||query.Status!=null&&query.Status is not("open" or "resolved"))return ReviewResult<PagedResult<ReviewReportResponse>>.Fail(400,"review_query_invalid");
  var f=Builders<ReviewReport>.Filter.Where(r=>query.Status==null||r.Status==query.Status);var total=await c.Reports.CountDocumentsAsync(f,cancellationToken:ct);
  var rows=await c.Reports.Find(f).SortByDescending(r=>r.CreatedAt).ThenByDescending(r=>r.Id).Skip((query.Page-1)*query.PageSize).Limit(query.PageSize).ToListAsync(ct);
  return new(new(rows.Select(r=>new ReviewReportResponse(r.Id,r.ReviewId,r.Reason,r.Status,r.Version,r.Resolution,r.ResolutionReason,r.CreatedAt,r.ResolvedAt)).ToArray(),query.Page,query.PageSize,total));
 }
}
