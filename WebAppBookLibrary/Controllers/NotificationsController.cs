using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController, Route("api/notifications/my"), Authorize]
public sealed class NotificationsController(NotificationService service) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] long? before, [FromQuery] int pageSize = 20, [FromQuery] bool? read = null, CancellationToken ct = default)
    {
        if (!await service.IsActiveAsync(UserId, ct)) return Error(401);
        if (pageSize is < 1 or > 100 || before is <= 0) return Error(400);
        return Ok(await service.ListAsync(UserId, before, pageSize, read, ct));
    }
    [HttpGet("unread-count")]
    public async Task<IActionResult> Count(CancellationToken ct) => !await service.IsActiveAsync(UserId, ct) ? Error(401) : Ok(new { count = await service.UnreadCountAsync(UserId, ct) });
    [HttpPut("{id}/read")]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        if (!await service.IsActiveAsync(UserId, ct)) return Error(401);
        return await service.ReadAsync(UserId, id, ct) ? NoContent() : Error(404);
    }
    [HttpPut("read-all")]
    public async Task<IActionResult> ReadAll(ReadNotificationsRequest request, CancellationToken ct)
    {
        if (!await service.IsActiveAsync(UserId, ct)) return Error(401);
        if (request.Through < 0) return Error(400);
        await service.ReadAllAsync(UserId, request.Through, ct); return NoContent();
    }
    [HttpGet("preferences")]
    public async Task<IActionResult> Preferences(CancellationToken ct) => !await service.IsActiveAsync(UserId, ct) ? Error(401) : Ok(await service.GetPreferencesAsync(UserId, ct));
    [HttpPut("preferences")]
    public async Task<IActionResult> Preferences(SaveNotificationPreferences request, CancellationToken ct)
    {
        if (!await service.IsActiveAsync(UserId, ct)) return Error(401);
        return await service.SavePreferencesAsync(UserId, request, ct) ? Ok(await service.GetPreferencesAsync(UserId, ct)) : Error(409);
    }
    private ObjectResult Error(int status)
    {
        var problem = ApiProblemFactory.Create(HttpContext, status, status == 409 ? "Verifica tu correo antes de activar los avisos por correo." : "No se pudo procesar la notificación.");
        problem.Extensions["code"] = "notification_" + status;
        var result = new ObjectResult(problem) { StatusCode = status }; result.ContentTypes.Add("application/problem+json"); return result;
    }
}
