namespace WebAppBookLibrary.Contracts.Reviews;
public record SaveReview(int Rating,string Text,long ExpectedVersion);
public record RemoveReview(long ExpectedVersion);
public record ReportReview(string Reason);
public record SetReviewVisibility(string Visibility,string Reason,long ExpectedVersion);
public record ResolveReviewReport(string Resolution,string Reason,long ExpectedVersion);
public record ReviewQuery(int Page=1,int PageSize=20,string? Visibility=null,string? BookId=null);
public record ReviewReportQuery(int Page=1,int PageSize=20,string? Status=null);
