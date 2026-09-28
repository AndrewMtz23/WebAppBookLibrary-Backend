using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebAppBookLibrary.Contracts.Reading;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController, Route("api/reading/my"), Authorize]
public sealed class ReadingController(ReadingService service) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ReadingQuery query, CancellationToken ct)
    {
        if (!await service.IsActiveAsync(UserId, ct)) return Error(401, "reading_account_unavailable");
        if (!ReadingService.ValidQuery(query)) return Error(400, "reading_invalid");
        return Ok(await service.ListAsync(UserId, query, ct));
    }
    [HttpGet("latest")]
    public async Task<IActionResult> Latest(CancellationToken ct) => !await service.IsActiveAsync(UserId, ct)
        ? Error(401, "reading_account_unavailable") : Ok(new LatestReadingResponse(await service.LatestAsync(UserId, ct)));
    [HttpPut("books/{bookId}")]
    public async Task<IActionResult> Save(string bookId, SaveReadingRequest request, CancellationToken ct)
    {
        if (!await service.IsActiveAsync(UserId, ct)) return Error(401, "reading_account_unavailable");
        return Mutation(await service.SaveAsync(UserId, bookId, request, ct));
    }
    [HttpDelete("books/{bookId}")]
    public async Task<IActionResult> Remove(string bookId, [FromQuery] string expectedRevision, CancellationToken ct)
    {
        if (!await service.IsActiveAsync(UserId, ct)) return Error(401, "reading_account_unavailable");
        return Mutation(await service.DeleteAsync(UserId, bookId, expectedRevision, ct));
    }
    private IActionResult Mutation(ReadingMutationResult result) => result.Status switch {
        200 => Ok(result.Entry), 204 => NoContent(), _ => Error(result.Status, result.ErrorCode ?? "reading_invalid")
    };
    private ObjectResult Error(int status, string code)
    {
        var problem = ApiProblemFactory.Create(HttpContext, status, status == 409 ? "La lectura cambió. Recarga antes de guardar." : "No se pudo procesar la lectura.");
        problem.Extensions["code"] = code;
        var result = new ObjectResult(problem) { StatusCode = status }; result.ContentTypes.Add("application/problem+json"); return result;
    }
}
