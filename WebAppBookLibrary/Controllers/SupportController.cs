using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Contracts.Support;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Services.Support;
namespace WebAppBookLibrary.Controllers;

public abstract class SupportApiController : ControllerBase
{
    protected string ActorId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    protected IActionResult Result<T>(SupportResult<T> result)
    {
        if (result.Status == 200) return Ok(result.Value);
        if (result.RetryAfterSeconds.HasValue) Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var text = result.Code switch {
            "support_open_limit" => "Tienes el máximo de solicitudes abiertas. Espera su atención antes de crear otra.",
            "support_rate_limit" => "Alcanzaste el límite de envíos. Conserva tu borrador e intenta más tarde.",
            "support_intake_disabled" => "La creación de solicitudes está temporalmente suspendida. Puedes consultar las existentes.",
            _ => result.Status switch { 409 => "La solicitud cambió. Actualiza antes de volver a enviar.", 403 => "No tienes permiso para esta operación.", 404 => "La solicitud o referencia no está disponible.", 401 => "Inicia sesión con una cuenta activa.", _ => "Revisa los datos de la solicitud." }
        };
        var problem = ApiProblemFactory.Create(HttpContext, result.Status, text); problem.Extensions["code"] = result.Code;
        var response = new ObjectResult(problem) { StatusCode = result.Status }; response.ContentTypes.Add("application/problem+json"); return response;
    }
}

[ApiController, Route("api/support/tickets"), Authorize]
public sealed class SupportController(MongoSupportStore store, SupportQueries queries) : SupportApiController
{
    [HttpPost] public async Task<IActionResult> Create(CreateSupportTicket request, CancellationToken ct) => Result(await store.CreateAsync(ActorId, request, ct));
    [HttpGet("my")] public async Task<IActionResult> Mine([FromQuery] SupportQuery query, CancellationToken ct) => Result(await queries.ListMineAsync(ActorId, query, ct));
    [HttpGet("{id}")] public async Task<IActionResult> Detail(string id, int page = 1, int pageSize = 20, CancellationToken ct = default) => Result(await queries.GetMineAsync(ActorId, id, page, pageSize, ct));
    [HttpPost("{id}/replies")] public async Task<IActionResult> Reply(string id, AddSupportMessage request, CancellationToken ct) => Result(await store.ReplyAsync(ActorId, id, request, false, ct));
    [HttpPut("{id}/status")] public async Task<IActionResult> Status(string id, ChangeSupportStatus request, CancellationToken ct) => Result(await store.ChangeStatusAsync(ActorId, id, request, false, ct));
}
