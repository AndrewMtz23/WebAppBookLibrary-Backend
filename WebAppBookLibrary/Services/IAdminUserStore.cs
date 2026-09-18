using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public interface IAdminUserStore
{
    Task<PagedResult<User>> SearchAsync(AdminUserQuery query, CancellationToken token);
    Task<User?> FindByIdAsync(string id, CancellationToken token);
    Task<long> CountActiveAdminsAsync(CancellationToken token);
    Task<bool> TrySetRoleAsync(string id, string role, DateTime updatedAtUtc, CancellationToken token);
    Task<bool> TrySetStatusAsync(string id, bool active, DateTime updatedAtUtc, CancellationToken token);
    Task<AdminStoreMutationResult> SetRoleSafelyAsync(string actorId, string targetId, string role, DateTime updatedAtUtc, CancellationToken token);
    Task<AdminStoreMutationResult> SetStatusSafelyAsync(string actorId, string targetId, bool active, DateTime updatedAtUtc, CancellationToken token);
    Task<AdminStoreMutationResult> DeletePermanentlyAsync(string actorId, string targetId, CancellationToken token);
    Task<AdminStoreMutationResult> UpdateSafelyAsync(string actorId, string targetId, AdminUserUpdateCommand command, DateTime updatedAtUtc, CancellationToken token);
}

public sealed record AdminUserUpdateCommand(string Username, string DisplayName, string Email, string? AvatarUrl, string Role, bool IsActive, DateTime ExpectedUpdatedAt);

public enum AdminStoreMutationOutcome { Success, NotFound, SelfMutation, LastAdmin, ActorInvalid, IdentityConflict, Conflict, Unavailable, MustBeInactive, HasLoans }

public sealed record AdminStoreMutationResult(
    AdminStoreMutationOutcome Outcome,
    string? ActorUsername = null,
    string? TargetUsername = null,
    string? PreviousRole = null,
    bool? PreviousIsActive = null,
    string? NewRole = null,
    bool? NewIsActive = null,
    User? UpdatedUser = null)
{
    public static readonly AdminStoreMutationResult Success = new(AdminStoreMutationOutcome.Success);
    public static readonly AdminStoreMutationResult NotFound = new(AdminStoreMutationOutcome.NotFound);
    public static readonly AdminStoreMutationResult SelfMutation = new(AdminStoreMutationOutcome.SelfMutation);
    public static readonly AdminStoreMutationResult LastAdmin = new(AdminStoreMutationOutcome.LastAdmin);
    public static readonly AdminStoreMutationResult ActorInvalid = new(AdminStoreMutationOutcome.ActorInvalid);
    public static readonly AdminStoreMutationResult Conflict = new(AdminStoreMutationOutcome.Conflict);
    public static readonly AdminStoreMutationResult Unavailable = new(AdminStoreMutationOutcome.Unavailable);
}
