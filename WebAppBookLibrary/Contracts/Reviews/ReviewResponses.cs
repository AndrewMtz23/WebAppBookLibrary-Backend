using WebAppBookLibrary.Domain.Common;
namespace WebAppBookLibrary.Contracts.Reviews;
public record ReviewResult<T>(T? Value,string? Code=null,int Status=200,int? RetryAfterSeconds=null)
{
 public static ReviewResult<T> Fail(int status,string code,int? retry=null) => new(default,code,status,retry);
}
public record PublicReview(string Id,string DisplayName,int Rating,string Text,DateTime CreatedAt,DateTime UpdatedAt);
public record PublicReviewPage(IReadOnlyList<PublicReview> Items,int Page,int PageSize,long Total,double? AverageRating);
public record MyReview(string Id,string BookId,int? Rating,string Text,string Visibility,long Version,string? ModerationReason);
public record ReviewHistoryEntry(string Id,string Action,string Reason,DateTime CreatedAt);
public record AdminReviewDetail(MyReview Review,PagedResult<ReviewHistoryEntry> History);
public record ReviewReportResponse(string Id,string ReviewId,string Reason,string Status,long Version,string? Resolution,string? ResolutionReason,DateTime CreatedAt,DateTime? ResolvedAt);
