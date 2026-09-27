using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController, Route("api/profile/me/password"), Authorize]
public sealed class PasswordSecurityController(PasswordSecurityService passwords, Logservice audit) : ControllerBase
{
    [HttpPut, EnableRateLimiting("auth")]
    public async Task<IActionResult> Change(ChangePasswordRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var error = await passwords.ChangeAsync(userId, CurrentAccountValidator.CredentialVersion(User), request.CurrentPassword, request.NewPassword, token);
        await audit.AuthenticationObservedAsync("password_changed", userId, new Dictionary<string, string> { ["result"] = error is null ? "success" : "failed", ["reasonCode"] = error ?? "password_changed" });
        if (error is null) return NoContent();
        var status = error == "session_changed" ? 409 : 400;
        var problem = ApiProblemFactory.Create(HttpContext, status, "No se pudo cambiar la contraseña.");
        problem.Extensions["code"] = error;
        return new ObjectResult(problem) { StatusCode = status };
    }
}

public sealed class ChangePasswordRequest
{
    [Required, StringLength(1024)] public string CurrentPassword { get; init; } = "";
    [Required, StringLength(1024)] public string NewPassword { get; init; } = "";
}
