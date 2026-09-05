using MongoDB.Driver;
using WebAppBookLibrary.Models;

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
    public async Task<IReadOnlyList<Favorite>> ListAsync(string userId, CancellationToken token) =>
        await _favorites.Find(item => item.UserId == userId).SortByDescending(item => item.CreatedAt).ToListAsync(token);
    public Task InsertAsync(Favorite favorite, CancellationToken token) => _favorites.InsertOneAsync(favorite, cancellationToken: token);
    public async Task<bool> DeleteAsync(string userId, string bookId, CancellationToken token) =>
        (await _favorites.DeleteOneAsync(item => item.UserId == userId && item.BookId == bookId, token)).DeletedCount == 1;
}
