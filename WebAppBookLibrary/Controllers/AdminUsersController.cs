using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = PolicyNames.ManageUsers)]
[ServiceFilter(typeof(AdminMutationValidationAuditFilter))]
public sealed class AdminUsersController : ControllerBase
{
    private readonly AdminUserService service;
    private readonly IAdminUserAudit audit;
    public AdminUsersController(AdminUserService service, IAdminUserAudit audit) { this.service = service; this.audit = audit; }
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery(Name = "")] AdminUserQuery query, CancellationToken token) => Ok(await service.SearchAsync(query, token));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return ApiProblemFactory.Result(400, "Invalid user identifier");
        var user = await service.FindAsync(id, token);
        return user is null ? ApiProblemFactory.Result(404, "User not found") : Ok(user);
    }

    [HttpDelete("{id}/permanent")]
    public async Task<IActionResult> DeletePermanently(string id, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return await InvalidIdentifierAsync("user_delete_attempt");
        var actorId = CanonicalId(ActorId());
        var targetId = CanonicalId(id);
        var result = await service.DeletePermanentlyAsync(actorId, targetId, token);
        await audit.UserChangedAsync("user_delete_attempt", actorId, targetId, AuditMetadata(result, false));
        return result.ErrorCode switch
        {
            AdminUserErrorCodes.MustBeInactive => ApiProblemFactory.Result(409, "Deactivate the account before permanent deletion"),
            AdminUserErrorCodes.HasLoans => ApiProblemFactory.Result(409, "Accounts with loan history cannot be permanently deleted"),
            _ => MutationResult(result)
        };
    }

    [HttpPut("{id}/role")]
    public async Task<IActionResult> SetRole(string id, SetUserRoleRequest request, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return await InvalidIdentifierAsync("role_change_attempt");
        var actorId = CanonicalId(ActorId());
        var targetId = CanonicalId(id);
        var result = await service.SetRoleAsync(actorId, targetId, request.Role, DateTime.UtcNow, token);
        await audit.UserChangedAsync("role_change_attempt", actorId, targetId, AuditMetadata(result, true));
        return MutationResult(result);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, UpdateAdminUserRequest request, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return await InvalidIdentifierAsync("user_update_attempt");
        var actorId = CanonicalId(ActorId());
        var targetId = CanonicalId(id);
        var result = await service.UpdateAsync(actorId, targetId, request, DateTime.UtcNow, token);
        await audit.UserChangedAsync("user_update_attempt", actorId, targetId, UpdateAuditMetadata(result));
        if (result.Success) return Ok(result.User);
        return result.ErrorCode switch
        {
            AdminUserErrorCodes.NotFound => ApiProblemFactory.Result(404, "User not found"),
            AdminUserErrorCodes.InvalidRequest => ApiProblemFactory.Result(400, "Invalid user details"),
            AdminUserErrorCodes.InvalidRole => ApiProblemFactory.Result(400, "Invalid role"),
            AdminUserErrorCodes.IdentityConflict => ApiProblemFactory.Result(409, "Username or email already exists"),
            AdminUserErrorCodes.ActorInvalid => ApiProblemFactory.Result(401, "Session is no longer valid"),
            AdminUserErrorCodes.Unavailable => ApiProblemFactory.Result(503, "User service is unavailable"),
            _ => ApiProblemFactory.Result(409, "User cannot be updated")
        };
    }

    [HttpPut("{id}/status")]
    public async Task<IActionResult> SetStatus(string id, SetUserStatusRequest request, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return await InvalidIdentifierAsync("status_change_attempt");
        var actorId = CanonicalId(ActorId());
        var targetId = CanonicalId(id);
        var result = await service.SetStatusAsync(actorId, targetId, request.IsActive, DateTime.UtcNow, token);
        await audit.UserChangedAsync("status_change_attempt", actorId, targetId, AuditMetadata(result, false));
        return MutationResult(result);
    }

    private string ActorId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
    private static string CanonicalId(string id) => ObjectId.TryParse(id, out var value) ? value.ToString() : string.Empty;

    private async Task<IActionResult> InvalidIdentifierAsync(string action)
    {
        await audit.UserChangedAsync(action, CanonicalId(ActorId()), "invalid", new Dictionary<string, string> { ["result"] = "failed", ["reasonCode"] = "invalid_identifier" });
        return ApiProblemFactory.Result(400, "Invalid user identifier");
    }

    private static IReadOnlyDictionary<string, string> AuditMetadata(AdminUserMutationResult result, bool roleChange)
    {
        var metadata = new Dictionary<string, string>
        {
            ["result"] = result.Success ? "success" : "failed"
        };
        if (!result.Success && !string.IsNullOrWhiteSpace(result.ErrorCode)) metadata["reasonCode"] = result.ErrorCode;
        if (roleChange)
        {
            if (result.PreviousRole is not null) metadata["previousRole"] = result.PreviousRole;
            if (result.NewRole is not null) metadata["newRole"] = result.NewRole;
        }
        else
        {
            if (result.PreviousIsActive is not null) metadata["previousStatus"] = result.PreviousIsActive.Value ? "active" : "inactive";
            if (result.NewIsActive is not null) metadata["newStatus"] = result.NewIsActive.Value ? "active" : "inactive";
        }
        return metadata;
    }

    private static IReadOnlyDictionary<string, string> UpdateAuditMetadata(AdminUserUpdateResult result)
    {
        var metadata = new Dictionary<string, string> { ["result"] = result.Success ? "success" : "failed" };
        if (!result.Success && !string.IsNullOrWhiteSpace(result.ErrorCode)) metadata["reasonCode"] = result.ErrorCode;
        if (result.Mutation is { } mutation)
        {
            if (mutation.PreviousRole is not null) metadata["previousRole"] = mutation.PreviousRole;
            if (mutation.NewRole is not null) metadata["newRole"] = mutation.NewRole;
            if (mutation.PreviousIsActive is not null) metadata["previousStatus"] = mutation.PreviousIsActive.Value ? "active" : "inactive";
            if (mutation.NewIsActive is not null) metadata["newStatus"] = mutation.NewIsActive.Value ? "active" : "inactive";
        }
        return metadata;
    }

    private static IActionResult MutationResult(AdminUserMutationResult result)
    {
        if (result.Success) return new NoContentResult();
        return result.ErrorCode switch
        {
            AdminUserErrorCodes.NotFound => ApiProblemFactory.Result(404, "User not found"),
            AdminUserErrorCodes.InvalidRole => ApiProblemFactory.Result(400, "Invalid role"),
            AdminUserErrorCodes.ActorInvalid => ApiProblemFactory.Result(401, "Session is no longer valid"),
            AdminUserErrorCodes.Unavailable => ApiProblemFactory.Result(503, "User service is unavailable"),
            _ => ApiProblemFactory.Result(409, "User cannot be updated")
        };
    }
}
