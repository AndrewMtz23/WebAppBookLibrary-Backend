using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;

namespace WebAppBookLibrary.Services;

public sealed class AdminUserService(IAdminUserStore store)
{
    public async Task<PagedResult<AdminUserResponse>> SearchAsync(AdminUserQuery query, CancellationToken token)
    {
        var page = await store.SearchAsync(query, token);
        return new(page.Items.Select(Map).ToArray(), page.Page, page.PageSize, page.TotalItems);
    }

    public async Task<AdminUserResponse?> FindAsync(string id, CancellationToken token)
    {
        var user = await store.FindByIdAsync(id, token);
        return user is null ? null : Map(user);
    }

    public async Task<AdminUserMutationResult> SetRoleAsync(string actorId, string targetId, string role, DateTime at, CancellationToken token)
    {
        if (!RoleNames.TryNormalize(role, out var normalized)) return new(false, AdminUserErrorCodes.InvalidRole);
        if (actorId == targetId && normalized != RoleNames.Admin) return new(false, AdminUserErrorCodes.SelfMutation);
        var target = await store.FindByIdAsync(targetId, token);
        if (target is null) return new(false, AdminUserErrorCodes.NotFound);
        if (target.Role == RoleNames.Admin && target.IsActive && normalized != RoleNames.Admin && await store.CountActiveAdminsAsync(token) <= 1)
            return new(false, AdminUserErrorCodes.LastAdmin);
        return await store.TrySetRoleAsync(targetId, normalized, at, token) ? new(true, string.Empty) : new(false, AdminUserErrorCodes.Conflict);
    }

    public async Task<AdminUserMutationResult> SetStatusAsync(string actorId, string targetId, bool active, DateTime at, CancellationToken token)
    {
        if (actorId == targetId && !active) return new(false, AdminUserErrorCodes.SelfMutation);
        var target = await store.FindByIdAsync(targetId, token);
        if (target is null) return new(false, AdminUserErrorCodes.NotFound);
        if (target.Role == RoleNames.Admin && target.IsActive && !active && await store.CountActiveAdminsAsync(token) <= 1)
            return new(false, AdminUserErrorCodes.LastAdmin);
        return await store.TrySetStatusAsync(targetId, active, at, token) ? new(true, string.Empty) : new(false, AdminUserErrorCodes.Conflict);
    }

    private static AdminUserResponse Map(User user) => new(user.Id, user.Username, user.DisplayName, user.Email, user.Role, user.IsActive, user.CreatedAt, user.UpdatedAt, user.LastLoginAt);
}

public sealed record AdminUserMutationResult(bool Success, string ErrorCode);
public static class AdminUserErrorCodes
{
    public const string InvalidRole = "invalid_role";
    public const string SelfMutation = "self_mutation";
    public const string LastAdmin = "last_active_admin";
    public const string NotFound = "user_not_found";
    public const string Conflict = "concurrent_update_conflict";
}
