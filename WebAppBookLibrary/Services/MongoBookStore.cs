using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class MongoBookStore : IBookStore
{
    private readonly IMongoCollection<Book> _books;
    private readonly IMongoCollection<Loan> _loans;
    private readonly IMongoCollection<Favorite> _favorites;
    private readonly IMongoCollection<User> _users;
    public MongoBookStore(MongoDBService database)
    {
        _books = database.Books;
        _loans = database.Loans;
        _favorites = database.Favorites;
        _users = database.Users;
    }

    public async Task<PagedResult<BookCatalogEntry>> SearchAsync(NormalizedBookQuery query, bool includeInactive, string? viewerUsername, CancellationToken cancellationToken)
    {
        var builder = Builders<Book>.Filter;
        var filters = new List<FilterDefinition<Book>>();
        if (!includeInactive) filters.Add(builder.Or(builder.Eq(book => book.IsActive, true), builder.Exists(book => book.IsActive, false)));
        if (query.Query is not null) filters.Add(builder.Text(query.Query));
        if (query.Genre is not null) filters.Add(builder.AnyEq(book => book.Genres, query.Genre));
        if (query.MediaType is not null)
            filters.Add(query.MediaType == MediaTypes.Physical
                ? builder.Or(builder.Eq(book => book.MediaType, query.MediaType), builder.Exists(book => book.MediaType, false))
                : builder.Eq(book => book.MediaType, query.MediaType));
        if (query.Language is not null) filters.Add(builder.Eq(book => book.Language, query.Language));
        if (query.Available is not null)
        {
            var available = builder.Or(
                builder.Eq(book => book.MediaType, MediaTypes.Digital),
                builder.Gt(book => book.AvailableCopies, 0),
                builder.And(builder.Exists(book => book.AvailableCopies, false), builder.Eq(book => book.IsAvailable, true)));
            filters.Add(query.Available.Value ? available : builder.Not(available));
        }
        var filter = filters.Count == 0 ? builder.Empty : builder.And(filters);
        var total = await _books.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var field = query.Sort switch { "title" => "Title", "publishedDate" => "PublishedDate", _ => "CreatedAt" };
        var sort = query.Direction == "asc" ? Builders<Book>.Sort.Ascending(field).Ascending(book => book.Id) : Builders<Book>.Sort.Descending(field).Descending(book => book.Id);
        var books = await _books.Find(filter).Sort(sort).Skip((query.Page - 1) * query.PageSize).Limit(query.PageSize).ToListAsync(cancellationToken);
        var counts = await ReservationCountsAsync(books.Select(book => book.Id), cancellationToken);
        var favoriteIds = await FavoriteBookIdsAsync(viewerUsername, books.Select(book => book.Id), cancellationToken);
        return new(books.Select(book => new BookCatalogEntry(book, counts.GetValueOrDefault(book.Id), favoriteIds.Contains(book.Id))).ToArray(), query.Page, query.PageSize, total);
    }

    public async Task<BookCatalogEntry?> FindCatalogEntryAsync(string id, bool includeInactive, string? viewerUsername, CancellationToken cancellationToken)
    {
        var filter = Builders<Book>.Filter.Eq(book => book.Id, id);
        if (!includeInactive) filter &= Builders<Book>.Filter.Or(Builders<Book>.Filter.Eq(book => book.IsActive, true), Builders<Book>.Filter.Exists(book => book.IsActive, false));
        var book = await _books.Find(filter).FirstOrDefaultAsync(cancellationToken);
        if (book is null) return null;
        var count = await _loans.CountDocumentsAsync(loan => loan.BookId == id, cancellationToken: cancellationToken);
        var favoriteIds = await FavoriteBookIdsAsync(viewerUsername, [id], cancellationToken);
        return new(book, count, favoriteIds.Contains(id));
    }

    public async Task<Book?> FindByIdAsync(string id, CancellationToken cancellationToken) => (Book?)await _books.Find(book => book.Id == id).FirstOrDefaultAsync(cancellationToken);
    public Task<bool> IsbnExistsAsync(string normalizedIsbn, string? excludingId, CancellationToken cancellationToken) => _books.Find(book => book.Isbn == normalizedIsbn && book.Id != excludingId).AnyAsync(cancellationToken);
    public Task InsertAsync(Book book, CancellationToken cancellationToken) => _books.InsertOneAsync(book, cancellationToken: cancellationToken);
    public async Task<int> CountActivePhysicalLoansAsync(string bookId, CancellationToken cancellationToken)
    {
        var builder = Builders<Loan>.Filter;
        var active = builder.In(loan => loan.Status, [WebAppBookLibrary.Domain.Loans.LoanStatuses.Active, WebAppBookLibrary.Domain.Loans.LoanStatuses.Overdue]) |
                     (builder.Exists(loan => loan.Status, false) & builder.Eq(loan => loan.IsReturned, false));
        var physical = builder.Eq(loan => loan.MediaType, MediaTypes.Physical) | builder.Exists(loan => loan.MediaType, false);
        var filter = builder.Eq(loan => loan.BookId, bookId) & active & physical;
        return checked((int)await _loans.CountDocumentsAsync(filter, cancellationToken: cancellationToken));
    }
    public async Task<bool> ReplaceMetadataAsync(Book book, DateTime expectedUpdatedAt, CancellationToken cancellationToken)
    {
        var version = Builders<Book>.Filter.Eq(item => item.UpdatedAt, expectedUpdatedAt) |
                      Builders<Book>.Filter.Exists(item => item.UpdatedAt, false);
        var filter = Builders<Book>.Filter.Eq(item => item.Id, book.Id) & version;
        return (await _books.ReplaceOneAsync(filter, book, cancellationToken: cancellationToken)).MatchedCount == 1;
    }
    public async Task<bool> SetActiveAsync(string id, bool active, DateTime updated, CancellationToken token) => (await _books.UpdateOneAsync(book => book.Id == id, Builders<Book>.Update.Set(book => book.IsActive, active).Set(book => book.UpdatedAt, updated), cancellationToken: token)).MatchedCount == 1;

    private async Task<Dictionary<string, long>> ReservationCountsAsync(IEnumerable<string> bookIds, CancellationToken token)
    {
        var ids = bookIds.ToArray();
        if (ids.Length == 0) return [];
        return await _loans.Aggregate()
            .Match(Builders<Loan>.Filter.In(loan => loan.BookId, ids))
            .Group(loan => loan.BookId, group => new { BookId = group.Key, Count = group.LongCount() })
            .ToListAsync(token)
            .ContinueWith(task => task.Result.ToDictionary(item => item.BookId, item => item.Count), token);
    }

    private async Task<HashSet<string>> FavoriteBookIdsAsync(string? username, IEnumerable<string> bookIds, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(username)) return [];
        var user = await _users.Find(item => item.Username == username).FirstOrDefaultAsync(token);
        if (user is null) return [];
        var ids = bookIds.ToArray();
        var favorites = await _favorites.Find(item => item.UserId == user.Id && ids.Contains(item.BookId)).Project(item => item.BookId).ToListAsync(token);
        return favorites.ToHashSet(StringComparer.Ordinal);
    }
}
