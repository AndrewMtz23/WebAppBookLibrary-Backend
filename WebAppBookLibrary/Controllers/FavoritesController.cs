using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Contracts.Favorites;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/favorites")]
[Authorize(Policy = PolicyNames.BorrowBooks)]
public sealed class FavoritesController : ControllerBase
{
    private readonly FavoriteService service;
    private readonly Logservice log;
    public FavoritesController(FavoriteService service, Logservice log) { this.service = service; this.log = log; }
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] FavoriteQuery query, CancellationToken token)
    {
        var result = await service.ListAsync(User.Identity?.Name ?? string.Empty, query, token);
        return result is null ? ApiProblemFactory.Result(403, "Favorites are not permitted") : Ok(result);
    }

    [HttpPost("{bookId}")]
    public async Task<IActionResult> Add(string bookId, CancellationToken token)
    {
        if (!ObjectId.TryParse(bookId, out _)) return ApiProblemFactory.Result(400, "Invalid book identifier");
        var result = await service.AddAsync(User.Identity?.Name ?? string.Empty, bookId, DateTime.UtcNow, token);
        if (!result.Success) return result.ErrorCode == FavoriteErrorCodes.BookNotFound
            ? ApiProblemFactory.Result(404, "Book not found")
            : ApiProblemFactory.Result(403, "Favorite is not permitted");
        if (!result.Idempotent) await log.UserChangedAsync("favorite_added", User.Identity?.Name ?? string.Empty, result.Favorite!.Id, new Dictionary<string, string> { ["operation"] = "favorite" });
        return result.Idempotent ? Ok(result.Favorite) : StatusCode(StatusCodes.Status201Created, result.Favorite);
    }

    [HttpDelete("{bookId}")]
    public async Task<IActionResult> Remove(string bookId, CancellationToken token)
    {
        if (!ObjectId.TryParse(bookId, out _)) return ApiProblemFactory.Result(400, "Invalid book identifier");
        var result = await service.RemoveAsync(User.Identity?.Name ?? string.Empty, bookId, token);
        if (!result.Success) return ApiProblemFactory.Result(403, "Favorite is not permitted");
        if (!result.Idempotent) await log.UserChangedAsync("favorite_removed", User.Identity?.Name ?? string.Empty, bookId, new Dictionary<string, string> { ["operation"] = "favorite" });
        return NoContent();
    }
}
