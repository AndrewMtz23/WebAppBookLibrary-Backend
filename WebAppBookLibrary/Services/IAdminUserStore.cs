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
}

public enum AdminStoreMutationResult { Success, NotFound, SelfMutation, LastAdmin, Conflict }
