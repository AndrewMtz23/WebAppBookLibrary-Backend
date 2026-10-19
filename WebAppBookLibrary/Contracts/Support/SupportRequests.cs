namespace WebAppBookLibrary.Contracts.Support;
public sealed record CreateSupportTicket(string Category, string Subject, string Description, string? BookId, string? LoanId, string IdempotencyKey);
public sealed record AddSupportMessage(string Text, long ExpectedVersion);
public sealed record ChangeSupportStatus(string Status, string Reason, long ExpectedVersion);
public sealed record AssignSupportTicket(string? AssigneeId, long ExpectedVersion);
public sealed record SupportQuery(int Page = 1, int PageSize = 20, string? Status = null, string? Category = null);
public sealed record SupportResult<T>(T? Value, string? Code = null, int Status = 200, int? RetryAfterSeconds = null)
{
    public static SupportResult<T> Fail(int status, string code, int? retry = null) => new(default, code, status, retry);
}
