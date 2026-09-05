using System.ComponentModel.DataAnnotations;

namespace WebAppBookLibrary.Contracts.Admin;

public sealed record AdminUserResponse(string Id, string Username, string DisplayName, string Email, string Role, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastLoginAt);

public sealed class AdminUserQuery
{
    public string? Query { get; init; }
    public string? Role { get; init; }
    public bool? IsActive { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public AdminUserQuery Normalize() => new() { Query = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim(), Role = string.IsNullOrWhiteSpace(Role) ? null : Role.Trim().ToLowerInvariant(), IsActive = IsActive, Page = Math.Max(1, Page), PageSize = Math.Clamp(PageSize, 1, 100) };
}

public sealed record SetUserRoleRequest([property: Required] string Role);
public sealed record SetUserStatusRequest(bool IsActive);
