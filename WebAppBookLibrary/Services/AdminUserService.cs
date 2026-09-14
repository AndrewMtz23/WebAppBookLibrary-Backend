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

    public async Task<AdminUserUpdateResult> UpdateAsync(string actorId, string targetId, UpdateAdminUserRequest request, DateTime at, CancellationToken token)
    {
        if (!RoleNames.TryNormalize(request.Role, out var role))
            return new(false, AdminUserErrorCodes.InvalidRole);
        var username = request.Username.Trim();
        var displayName = request.DisplayName.Trim();
        var email = request.Email.Trim();
        var avatarUrl = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
        if (username.Length is < 3 or > 100 || displayName.Length is < 1 or > 120 || email.Length > 254 || !EmailValidator.IsValid(email) ||
            request.ExpectedUpdatedAt == default || !ValidAvatarUrl(avatarUrl))
            return new(false, AdminUserErrorCodes.InvalidRequest);

        var command = new AdminUserUpdateCommand(username, displayName, email, avatarUrl, role, request.IsActive, request.ExpectedUpdatedAt.ToUniversalTime());
        var result = await store.UpdateSafelyAsync(actorId, targetId, command, at, token);
        var mutation = Map(result);
        return new(mutation.Success, mutation.ErrorCode, result.UpdatedUser is null ? null : Map(result.UpdatedUser), mutation);
    }

    private static bool ValidAvatarUrl(string? value) =>
        value is null || Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static AdminUserResponse Map(User user) => new(user.Id, user.Username, user.DisplayName, user.Email, user.AvatarUrl, user.Role, user.IsActive, user.CreatedAt, user.UpdatedAt, user.LastLoginAt);
    private static AdminUserMutationResult Map(AdminStoreMutationResult result) => new(
        result.Outcome == AdminStoreMutationOutcome.Success,
        result.Outcome switch
    {
        AdminStoreMutationOutcome.Success => string.Empty,
        AdminStoreMutationOutcome.NotFound => AdminUserErrorCodes.NotFound,
        AdminStoreMutationOutcome.SelfMutation => AdminUserErrorCodes.SelfMutation,
        AdminStoreMutationOutcome.LastAdmin => AdminUserErrorCodes.LastAdmin,
        AdminStoreMutationOutcome.ActorInvalid => AdminUserErrorCodes.ActorInvalid,
        AdminStoreMutationOutcome.IdentityConflict => AdminUserErrorCodes.IdentityConflict,
        AdminStoreMutationOutcome.Unavailable => AdminUserErrorCodes.Unavailable,
        _ => AdminUserErrorCodes.Conflict
    }, result.ActorUsername, result.TargetUsername, result.PreviousRole, result.PreviousIsActive, result.NewRole, result.NewIsActive);
}

public sealed record AdminUserMutationResult(
    bool Success,
    string ErrorCode,
    string? ActorUsername = null,
    string? TargetUsername = null,
    string? PreviousRole = null,
    bool? PreviousIsActive = null,
    string? NewRole = null,
    bool? NewIsActive = null);
public sealed record AdminUserUpdateResult(bool Success, string ErrorCode, AdminUserResponse? User = null, AdminUserMutationResult? Mutation = null);
public static class AdminUserErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidRole = "invalid_role";
    public const string SelfMutation = "self_mutation";
    public const string LastAdmin = "last_active_admin";
    public const string NotFound = "user_not_found";
    public const string Conflict = "concurrent_update_conflict";
    public const string IdentityConflict = "username_or_email_already_exists";
    public const string ActorInvalid = "actor_no_longer_active_admin";
    public const string Unavailable = "user_store_unavailable";
}
