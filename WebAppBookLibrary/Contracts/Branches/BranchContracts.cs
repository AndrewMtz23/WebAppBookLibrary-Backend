using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Contracts.Branches;

public sealed record CreateBranchRequest(string Code, string Name, string TimeZone);
public sealed record UpdateBranchRequest(string Name, string TimeZone, bool IsActive, long ExpectedVersion);
public sealed record AssignBranchRequest(bool IsActive, long ExpectedVersion);
public sealed record BranchResult<T>(bool Success, string? ErrorCode, T? Value = default);
public sealed record BranchAccess(Branch Branch, string ActorId, bool IsAdmin);
public sealed record PublicBranch(string Id, string Code, string Name, string TimeZone);
