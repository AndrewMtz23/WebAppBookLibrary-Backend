using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/favorites")]
[Authorize(Policy = PolicyNames.BorrowBooks)]
public sealed class FavoritesController(FavoriteService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        var result = await service.ListAsync(User.Identity?.Name ?? string.Empty, token);
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
        return result.Idempotent ? Ok(result.Favorite) : StatusCode(StatusCodes.Status201Created, result.Favorite);
    }

    [HttpDelete("{bookId}")]
    public async Task<IActionResult> Remove(string bookId, CancellationToken token)
    {
        if (!ObjectId.TryParse(bookId, out _)) return ApiProblemFactory.Result(400, "Invalid book identifier");
        var result = await service.RemoveAsync(User.Identity?.Name ?? string.Empty, bookId, token);
        return result.Success ? NoContent() : ApiProblemFactory.Result(403, "Favorite is not permitted");
    }
}
