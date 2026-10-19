using WebAppBookLibrary.Domain.Common;
namespace WebAppBookLibrary.Contracts.Support;
public sealed record SupportTicketSummary(string Id, string Category, string Subject, string Status, long Version, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record SupportMessageResponse(string Id, string Author, string Text, DateTime CreatedAt);
public sealed record SupportHistoryResponse(string Id, string Action, string FromStatus, string ToStatus, string Reason, DateTime CreatedAt);
public sealed record SupportTicketDetail(string Id, string Category, string Subject, string Description, string? BookId, string Status, long Version, DateTime CreatedAt, DateTime UpdatedAt, DateTime? ResolvedAt, PagedResult<SupportMessageResponse> Replies, DateTime? ReopenUntil = null);
public sealed record StaffSupportDetail(SupportTicketDetail Ticket, string? AssigneeId, PagedResult<SupportMessageResponse> Notes, PagedResult<SupportHistoryResponse> History);
public sealed record SupportAssignee(string Id, string Name);
