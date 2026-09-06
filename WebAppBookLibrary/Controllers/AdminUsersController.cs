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
public sealed class AdminUsersController : ControllerBase
{
    private readonly AdminUserService service;
    private readonly Logservice log;
    public AdminUsersController(AdminUserService service, Logservice log) { this.service = service; this.log = log; }
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] AdminUserQuery query, CancellationToken token) => Ok(await service.SearchAsync(query, token));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return ApiProblemFactory.Result(400, "Invalid user identifier");
        var user = await service.FindAsync(id, token);
        return user is null ? ApiProblemFactory.Result(404, "User not found") : Ok(user);
    }

    [HttpPut("{id}/role")]
    public async Task<IActionResult> SetRole(string id, SetUserRoleRequest request, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return ApiProblemFactory.Result(400, "Invalid user identifier");
        var result = await service.SetRoleAsync(ActorId(), id, request.Role, DateTime.UtcNow, token);
        if (result.Success) await log.UserChangedAsync("role_changed", ActorId(), id, new Dictionary<string, string> { ["role"] = request.Role });
        return MutationResult(result);
    }

    [HttpPut("{id}/status")]
    public async Task<IActionResult> SetStatus(string id, SetUserStatusRequest request, CancellationToken token)
    {
        if (!ObjectId.TryParse(id, out _)) return ApiProblemFactory.Result(400, "Invalid user identifier");
        var result = await service.SetStatusAsync(ActorId(), id, request.IsActive, DateTime.UtcNow, token);
        if (result.Success) await log.UserChangedAsync("status_changed", ActorId(), id, new Dictionary<string, string> { ["status"] = request.IsActive ? "active" : "inactive" });
        return MutationResult(result);
    }

    private string ActorId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private static IActionResult MutationResult(AdminUserMutationResult result)
    {
        if (result.Success) return new NoContentResult();
        return result.ErrorCode switch
        {
            AdminUserErrorCodes.NotFound => ApiProblemFactory.Result(404, "User not found"),
            AdminUserErrorCodes.InvalidRole => ApiProblemFactory.Result(400, "Invalid role"),
            _ => ApiProblemFactory.Result(409, "User cannot be updated")
        };
    }
}
