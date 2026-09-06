using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Favorites;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Domain.Common;

namespace WebAppBookLibrary.Services;

public sealed class FavoriteService(IFavoriteStore store)
{
    public async Task<PagedResult<FavoriteResponse>?> ListAsync(string username, FavoriteQuery query, CancellationToken token)
    {
        var user = await store.FindActiveUserAsync(username, token);
        if (user is null) return null;
        var page = await store.ListAsync(user.Id, query, token);
        return new(page.Items.Select(Map).ToArray(), page.Page, page.PageSize, page.TotalItems);
    }

    public async Task<FavoriteOperationResult> AddAsync(string username, string bookId, DateTime nowUtc, CancellationToken token)
    {
        var user = await store.FindActiveUserAsync(username, token);
        if (user is null) return new(false, FavoriteErrorCodes.InvalidUser);
        if (!await store.ActiveBookExistsAsync(bookId, token)) return new(false, FavoriteErrorCodes.BookNotFound);
        var existing = await store.FindAsync(user.Id, bookId, token);
        if (existing is not null) return new(true, string.Empty, existing, true);
        var favorite = new Favorite { Id = ObjectId.GenerateNewId().ToString(), UserId = user.Id, BookId = bookId, CreatedAt = nowUtc };
        try
        {
            await store.InsertAsync(favorite, token);
            return new(true, string.Empty, favorite);
        }
        catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new(true, string.Empty, await store.FindAsync(user.Id, bookId, token), true);
        }
    }

    public async Task<FavoriteOperationResult> RemoveAsync(string username, string bookId, CancellationToken token)
    {
        var user = await store.FindActiveUserAsync(username, token);
        if (user is null) return new(false, FavoriteErrorCodes.InvalidUser);
        var deleted = await store.DeleteAsync(user.Id, bookId, token);
        return new(true, string.Empty, null, !deleted);
    }

    private static FavoriteResponse Map(Favorite item) => new(item.Id, item.BookId, item.CreatedAt);
}

public sealed record FavoriteOperationResult(bool Success, string ErrorCode, Favorite? Favorite = null, bool Idempotent = false);
public static class FavoriteErrorCodes
{
    public const string InvalidUser = "invalid_user";
    public const string BookNotFound = "book_not_found";
}
