using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Contracts.Favorites;
using WebAppBookLibrary.Domain.Common;

namespace WebAppBookLibrary.Services;

public sealed class MongoFavoriteStore : IFavoriteStore
{
    private readonly IMongoCollection<Favorite> _favorites;
    private readonly IMongoCollection<User> _users;
    private readonly IMongoCollection<Book> _books;
    public MongoFavoriteStore(MongoDBService database)
    {
        _favorites = database.Favorites;
        _users = database.Users;
        _books = database.Books;
    }

    public async Task<User?> FindActiveUserAsync(string username, CancellationToken token) =>
        await _users.Find(user => user.Username == username && user.IsActive).FirstOrDefaultAsync(token);
    public Task<bool> ActiveBookExistsAsync(string bookId, CancellationToken token) =>
        _books.Find(book => book.Id == bookId && book.IsActive).AnyAsync(token);
    public async Task<Favorite?> FindAsync(string userId, string bookId, CancellationToken token) =>
        await _favorites.Find(item => item.UserId == userId && item.BookId == bookId).FirstOrDefaultAsync(token);
    public async Task<PagedResult<Favorite>> ListAsync(string userId, FavoriteQuery raw, CancellationToken token)
    {
        var query = raw.Normalize();
        var filter = Builders<Favorite>.Filter.Eq(item => item.UserId, userId);
        var total = await _favorites.CountDocumentsAsync(filter, cancellationToken: token);
        var items = await _favorites.Find(filter).SortByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Skip((query.Page - 1) * query.PageSize).Limit(query.PageSize).ToListAsync(token);
        return new(items, query.Page, query.PageSize, total);
    }
    public async Task InsertAsync(Favorite favorite, CancellationToken token)
    {
        using var session = await _books.Database.Client.StartSessionAsync(cancellationToken: token);
        await session.WithTransactionAsync(async (transaction, ct) =>
        {
            var changed = await _books.UpdateOneAsync(transaction, b => b.Id == favorite.BookId && b.IsActive,
                Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1), cancellationToken: ct);
            if (changed.MatchedCount != 1) throw new BookReferenceUnavailableException();
            await _favorites.InsertOneAsync(transaction, favorite, cancellationToken: ct);
            return true;
        }, cancellationToken: token);
    }
    public async Task<bool> DeleteAsync(string userId, string bookId, CancellationToken token) =>
        (await _favorites.DeleteOneAsync(item => item.UserId == userId && item.BookId == bookId, token)).DeletedCount == 1;
}
