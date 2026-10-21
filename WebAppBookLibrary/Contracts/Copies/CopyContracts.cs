using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Contracts.Copies;

public sealed record CopySelection(string CopyId, long ExpectedVersion);
public sealed record CopyInspection(string Condition, string? Location, string? Notes);
public sealed record ReturnPhysicalRequest(CopySelection? Copy, CopyInspection? Inspection, string? IdempotencyKey);
public sealed record InspectCopyRequest(long ExpectedVersion, CopyInspection Inspection);
public sealed record CopyActor(string UserId, bool IsAdmin);
public sealed record CopyMutationResult(bool Success, string? ErrorCode = null, BookCopy? Copy = null, bool Idempotent = false);
public sealed record CreateCopyRequest(string InventoryCode, string Condition, string? Location, string? Notes, string IdempotencyKey);
public sealed record UpdateCopyRequest(long ExpectedVersion, string? Location, string? Notes);
public sealed record ChangeCopyStatusRequest(string Status, string Reason, long ExpectedVersion, string IdempotencyKey);
public sealed record CopyResponse(string Id, string BookId, string InventoryCode, string Status,
    string? Condition, bool InspectionPending, string? Location, string? Notes, long Version,
    DateTime CreatedAt, DateTime UpdatedAt, string? ActiveAssignmentType, string? ActiveAssignmentId, long? AssignmentVersion = null, string? ReaderDisplayName = null)
{
    public static CopyResponse From(BookCopy c) => new(c.Id,c.BookId,c.InventoryCode,c.Status,c.Condition,
        c.InspectionPending,c.Location,c.Notes,c.Version,c.CreatedAt,c.UpdatedAt,c.ActiveAssignmentType,c.ActiveAssignmentId);
}
public sealed record CopyPage(IReadOnlyList<CopyResponse> Items, int TotalCount, int Page, int PageSize,IReadOnlyDictionary<string,int>? StateCounts=null);
public sealed record CopyHistoryResponse(string Id,string Action,string? FromStatus,string ToStatus,string? ActorId,string? Reason,DateTime CreatedAt);
public sealed record CopyHistoryPage(IReadOnlyList<CopyHistoryResponse> Items,int TotalCount,int Page,int PageSize);
