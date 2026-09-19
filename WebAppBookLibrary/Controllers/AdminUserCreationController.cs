using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = PolicyNames.ManageUsers)]
public sealed class AdminUserCreationController(IUserStore users, IAdminUserAudit audit) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(CreateAdminUserRequest request)
    {
        var actor = await users.FindByUsernameAsync(User.Identity?.Name ?? "");
        if (actor?.IsActive != true || actor.Role != RoleNames.Admin || actor.Id != User.FindFirstValue(ClaimTypes.NameIdentifier))
            return ApiProblemFactory.Result(403, "Administrator access is required");
        var username = request.Username.Trim(); var name = request.DisplayName.Trim(); var email = request.Email.Trim();
        if (username.Length < 3 || name.Length == 0 || !EmailValidator.IsValid(email) || !PasswordValidator.IsValid(request.Password) || !RoleNames.TryNormalize(request.Role, out var role))
            return ApiProblemFactory.Result(400, "Revisa el nombre, correo, rol y contraseña.");
        if (await users.FindByUsernameOrEmailAsync(username, email) is not null)
            return ApiProblemFactory.Result(409, "El nombre de usuario o correo ya está registrado.");
        var at = new BsonDateTime(DateTime.UtcNow).ToUniversalTime();
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = username, NormalizedUsername = username.ToUpperInvariant(), DisplayName = name, Email = email, NormalizedEmail = email.ToUpperInvariant(), Role = role, IsActive = true, CreatedAt = at, UpdatedAt = at, PasswordHash = PasswordHasher.HashPassword(request.Password) };
        try { await users.InsertAsync(user); }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        { return ApiProblemFactory.Result(409, "El nombre de usuario o correo ya está registrado."); }
        await audit.UserChangedAsync("user_create", actor.Id, user.Id, new Dictionary<string, string> { ["result"] = "success", ["newRole"] = role });
        return Created($"/api/admin/users/{user.Id}", new AdminUserResponse(user.Id, user.Username, user.DisplayName, user.Email, user.AvatarUrl, user.Role, user.IsActive, user.CreatedAt, user.UpdatedAt, user.LastLoginAt));
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAdminUserRequest(
    [Required, StringLength(100, MinimumLength = 3)] string Username,
    [Required, StringLength(120, MinimumLength = 1)] string DisplayName,
    [Required, EmailAddress, StringLength(254)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [Required] string Role);
