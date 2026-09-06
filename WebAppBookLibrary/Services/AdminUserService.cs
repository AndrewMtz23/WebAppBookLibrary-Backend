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
        return Map(await store.SetRoleSafelyAsync(actorId, targetId, normalized, at, token));
    }

    public async Task<AdminUserMutationResult> SetStatusAsync(string actorId, string targetId, bool active, DateTime at, CancellationToken token)
    {
        return Map(await store.SetStatusSafelyAsync(actorId, targetId, active, at, token));
    }

    private static AdminUserResponse Map(User user) => new(user.Id, user.Username, user.DisplayName, user.Email, user.Role, user.IsActive, user.CreatedAt, user.UpdatedAt, user.LastLoginAt);
    private static AdminUserMutationResult Map(AdminStoreMutationResult result) => result switch
    {
        AdminStoreMutationResult.Success => new(true, string.Empty),
        AdminStoreMutationResult.NotFound => new(false, AdminUserErrorCodes.NotFound),
        AdminStoreMutationResult.SelfMutation => new(false, AdminUserErrorCodes.SelfMutation),
        AdminStoreMutationResult.LastAdmin => new(false, AdminUserErrorCodes.LastAdmin),
        _ => new(false, AdminUserErrorCodes.Conflict)
    };
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
