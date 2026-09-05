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
public sealed class AdminUsersController(AdminUserService service) : ControllerBase
{
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
    public async Task<IActionResult> SetRole(string id, SetUserRoleRequest request, CancellationToken token) =>
        MutationResult(await service.SetRoleAsync(ActorId(), id, request.Role, DateTime.UtcNow, token));

    [HttpPut("{id}/status")]
    public async Task<IActionResult> SetStatus(string id, SetUserStatusRequest request, CancellationToken token) =>
        MutationResult(await service.SetStatusAsync(ActorId(), id, request.IsActive, DateTime.UtcNow, token));

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
