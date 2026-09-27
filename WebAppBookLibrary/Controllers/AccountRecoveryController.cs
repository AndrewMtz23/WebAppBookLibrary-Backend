using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController, Route("api/auth"), EnableRateLimiting("auth")]
public sealed class AccountRecoveryController(AccountRecoveryService recovery, IOptions<AccountRecoveryOptions> options) : ControllerBase
{
    [HttpPost("password-reset/request"), AllowAnonymous]
    public async Task<IActionResult> RequestReset(RecoveryEmailRequest request, CancellationToken ct)
    {
        if (!options.Value.Enabled) return StatusCode(503, new { code = "mail_unavailable" });
        await recovery.RequestResetAsync(request.Email, ct);
        return Accepted(new { message = "Si existe una cuenta activa con ese correo, recibirás un enlace para restablecer tu contraseña." });
    }

    [HttpPost("password-reset/confirm"), AllowAnonymous]
    public async Task<IActionResult> Reset(RecoveryPasswordRequest request, CancellationToken ct) =>
        await recovery.ConfirmAsync("reset", request.Token, request.NewPassword, ct) ? NoContent() : BadRequest(new { code = "invalid_challenge" });

    [HttpPost("email-verification/request"), Authorize]
    public async Task<IActionResult> RequestVerification(CancellationToken ct)
    {
        if (!options.Value.Enabled) return StatusCode(503, new { code = "mail_unavailable" });
        return await recovery.RequestVerificationAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, ct)
            ? Accepted(new { message = "La solicitud de verificación está en cola." }) : StatusCode(429, new { code = "request_cooldown" });
    }

    [HttpPost("email-verification/confirm"), AllowAnonymous]
    public async Task<IActionResult> Verify(RecoveryTokenRequest request, CancellationToken ct) =>
        await recovery.ConfirmAsync("verify", request.Token, null, ct) ? NoContent() : BadRequest(new { code = "invalid_challenge" });
}

public sealed class RecoveryEmailRequest
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = "";
}
public class RecoveryTokenRequest
{
    [Required, StringLength(43, MinimumLength = 43)] public string Token { get; init; } = "";
}
public sealed class RecoveryPasswordRequest : RecoveryTokenRequest
{
    [Required, StringLength(1024)] public string NewPassword { get; init; } = "";
}
