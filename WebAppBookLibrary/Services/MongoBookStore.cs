using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class MongoBookStore : IBookStore
{
    // Conservative structural check, executed by Mongo so counts and pages agree.
    // Recognizes international DNS hosts, userinfo and IPv6, with ports 0..65535.
    // Deliberately does not claim network reachability or full System.Uri parity.
    private const string HttpsResourcePattern = @"^https://(?:[^\s/@?#\\]*@)?(?:[\p{L}\p{N}_](?:[\p{L}\p{N}\p{M}_-]*[\p{L}\p{N}\p{M}_])?(?:\.[\p{L}\p{N}_](?:[\p{L}\p{N}\p{M}_-]*[\p{L}\p{N}\p{M}_])?)*\.?|\[(?:(?:[a-f0-9]{1,4}:){7}[a-f0-9]{1,4}|(?:[a-f0-9]{1,4}:){1,7}:|(?:[a-f0-9]{1,4}:){1,6}:[a-f0-9]{1,4}|(?:[a-f0-9]{1,4}:){1,5}(?::[a-f0-9]{1,4}){1,2}|(?:[a-f0-9]{1,4}:){1,4}(?::[a-f0-9]{1,4}){1,3}|(?:[a-f0-9]{1,4}:){1,3}(?::[a-f0-9]{1,4}){1,4}|(?:[a-f0-9]{1,4}:){1,2}(?::[a-f0-9]{1,4}){1,5}|[a-f0-9]{1,4}:(?:(?::[a-f0-9]{1,4}){1,6})|:(?:(?::[a-f0-9]{1,4}){1,7}|:))\])(?::0*(?:[0-9]{1,4}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?(?:[/?#][\s\S]*)?$";
    private readonly IMongoCollection<Book> _books;
    private readonly IMongoCollection<Loan> _loans;
    private readonly IMongoCollection<Favorite> _favorites;
    private readonly IMongoCollection<User> _users;
    public MongoBookStore(MongoDBService database)
        : this(database.Books, database.Loans, database.Favorites, database.Users) { }
    public MongoBookStore(IMongoCollection<Book> books, IMongoCollection<Loan> loans, IMongoCollection<Favorite> favorites, IMongoCollection<User> users)
    {
        _books = books; _loans = loans; _favorites = favorites; _users = users;
    }

    public async Task<PagedResult<BookCatalogEntry>> SearchAsync(NormalizedBookQuery query, bool includeInactive, string? viewerUsername, CancellationToken cancellationToken)
    {
        FilterDefinition<Book> filter = RenderFilter(query, includeInactive);
        var total = await _books.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var offset = ((long)query.Page - 1) * query.PageSize;
        if (offset >= total) return new([], query.Page, query.PageSize, total);
        if (query.Sort == "reservationCount")
            return await SearchByPopularityAsync(filter, query, viewerUsername, total, cancellationToken);
        var field = query.Sort switch { "title" => "Title", "publishedDate" => "PublishedDate", _ => "CreatedAt" };
        var sort = query.Sort == "relevance"
            ? Builders<Book>.Sort.MetaTextScore("score").Ascending(book => book.Id)
            : query.Direction == "asc" ? Builders<Book>.Sort.Ascending(field).Ascending(book => book.Id) : Builders<Book>.Sort.Descending(field).Descending(book => book.Id);
        var books = offset <= int.MaxValue
            ? await _books.Find(filter).Sort(sort).Skip((int)offset).Limit(query.PageSize).ToListAsync(cancellationToken)
            : await _books.Aggregate().Match(filter).Sort(sort).Skip(offset).Limit(query.PageSize).ToListAsync(cancellationToken);
        var counts = await ReservationCountsAsync(books.Select(book => book.Id), cancellationToken);
        var favoriteIds = await FavoriteBookIdsAsync(viewerUsername, books.Select(book => book.Id), cancellationToken);
        return new(books.Select(book => new BookCatalogEntry(book, counts.GetValueOrDefault(book.Id), favoriteIds.Contains(book.Id))).ToArray(), query.Page, query.PageSize, total);
    }

    public static BsonDocument RenderFilter(NormalizedBookQuery query, bool includeInactive)
    {
        var builder = Builders<Book>.Filter;
        var filters = new List<FilterDefinition<Book>>();
        if (!includeInactive) filters.Add(builder.Or(builder.Eq(book => book.IsActive, true), builder.Exists(book => book.IsActive, false)));
        else if (query.IsActive is not null) filters.Add(builder.Eq(book => book.IsActive, query.IsActive.Value));
        if (query.Query is not null)
        {
            if (query.Sort == "relevance") filters.Add(builder.Text(query.Query));
            else
            {
                var regex = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query.Query), "i");
                filters.Add(builder.Or(builder.Regex(book => book.Title, regex), builder.Regex(book => book.Subtitle, regex), builder.Regex(book => book.Isbn, regex), builder.AnyStringIn(book => book.Authors, regex)));
            }
        }
        if (query.Genre is not null) filters.Add(builder.AnyEq(book => book.Genres, query.Genre));
        if (query.MediaType is not null) filters.Add(query.MediaType == MediaTypes.Physical ? builder.Or(builder.Eq(book => book.MediaType, query.MediaType), builder.Exists(book => book.MediaType, false)) : builder.Eq(book => book.MediaType, query.MediaType));
        if (query.Language is not null) filters.Add(builder.Eq(book => book.Language, query.Language));
        if (query.Available is not null)
        {
            var available = builder.Or(builder.Eq(book => book.MediaType, MediaTypes.Digital), builder.Gt(book => book.AvailableCopies, 0), builder.And(builder.Exists(book => book.AvailableCopies, false), builder.Eq(book => book.IsAvailable, true)));
            filters.Add(query.Available.Value ? available : builder.Not(available));
        }
        if (includeInactive && query.LowStock is not null)
        {
            var low = builder.And(builder.Eq(book => book.IsActive, true), builder.Eq(book => book.MediaType, MediaTypes.Physical), builder.Eq(book => book.AvailableCopies, 1));
            filters.Add(query.LowStock.Value ? low : builder.Not(low));
        }
        if (includeInactive && query.MissingResource is not null)
        {
            var missing = builder.And(builder.Eq(book => book.IsActive, true), builder.Eq(book => book.MediaType, MediaTypes.Digital), builder.Not(builder.Regex(book => book.DigitalResourceUrl, new BsonRegularExpression(HttpsResourcePattern, "i"))));
            filters.Add(query.MissingResource.Value ? missing : builder.Not(missing));
        }
        var filter = filters.Count == 0 ? builder.Empty : builder.And(filters);
        return filter.Render(new RenderArgs<Book>(BsonSerializer.SerializerRegistry.GetSerializer<Book>(), BsonSerializer.SerializerRegistry));
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
        using var session = await _books.Database.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await session.WithTransactionAsync(async (transaction, ct) =>
        {
            var version = Builders<Book>.Filter.Eq(item => item.UpdatedAt, expectedUpdatedAt) |
                          Builders<Book>.Filter.Exists(item => item.UpdatedAt, false);
            var filter = Builders<Book>.Filter.Eq(item => item.Id, book.Id) & version;
            var current = await _books.FindOneAndUpdateAsync(transaction, filter, Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1),
                new FindOneAndUpdateOptions<Book, Book> { ReturnDocument = ReturnDocument.After }, ct);
            if (current is null) return false;
            var held = current.MediaType == MediaTypes.Physical
                ? Math.Max(0, (current.TotalCopies ?? 1) - (current.AvailableCopies ?? (current.IsAvailable ? 1 : 0))) : 0;
            if (held > 0 && (book.MediaType != current.MediaType || book.TotalCopies < held)) return false;
            book.AvailableCopies = book.MediaType == MediaTypes.Physical ? book.TotalCopies - held : null;
            book.IsAvailable = book.MediaType == MediaTypes.Digital || book.AvailableCopies > 0;
            book.ReferenceVersion = current.ReferenceVersion;
            return (await _books.ReplaceOneAsync(transaction, b => b.Id == book.Id, book, cancellationToken: ct)).MatchedCount == 1;
        }, cancellationToken: cancellationToken);
    }
    public async Task<bool> SetActiveAsync(string id, bool active, DateTime updated, CancellationToken token) => (await _books.UpdateOneAsync(book => book.Id == id, Builders<Book>.Update.Set(book => book.IsActive, active).Set(book => book.UpdatedAt, updated), cancellationToken: token)).MatchedCount == 1;

    public async Task<BookMutationResult> DeletePermanentlyAsync(string id, CancellationToken token)
    {
        using var session = await _books.Database.Client.StartSessionAsync(cancellationToken: token);
        return await session.WithTransactionAsync(async (transaction, ct) =>
        {
            // A real write serializes this snapshot with every reference insertion.
            var book = await _books.FindOneAndUpdateAsync(transaction, Builders<Book>.Filter.Eq(b => b.Id, id),
                Builders<Book>.Update.Inc(b => b.ReferenceVersion, 1),
                new FindOneAndUpdateOptions<Book, Book> { ReturnDocument = ReturnDocument.After }, ct);
            if (book is null) return new BookMutationResult(false, "book_not_found");
            if (book.IsActive) return new BookMutationResult(false, "book_must_be_inactive");
            if (await _loans.Find(transaction, l => l.BookId == id).AnyAsync(ct) ||
                await _favorites.Find(transaction, f => f.BookId == id).AnyAsync(ct))
                return new BookMutationResult(false, "book_has_references");
            await _books.DeleteOneAsync(transaction, b => b.Id == id, cancellationToken: ct);
            return new BookMutationResult(true, string.Empty);
        }, cancellationToken: token);
    }

    public async Task<IReadOnlyList<BookFacetResponse>> GetGenreFacetsAsync(CancellationToken token)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("IsActive", true),
                new BsonDocument("IsActive", new BsonDocument("$exists", false))
            })),
            new BsonDocument("$unwind", "$Genres"),
            new BsonDocument("$match", new BsonDocument("Genres", new BsonDocument
            {
                { "$type", "string" },
                { "$ne", string.Empty }
            })),
            new BsonDocument("$group", new BsonDocument
            {
                { "_id", "$Genres" },
                { "count", new BsonDocument("$sum", 1) }
            }),
            new BsonDocument("$sort", new BsonDocument { { "count", -1 }, { "_id", 1 } })
        };
        var rows = await _books.Aggregate<BsonDocument>(pipeline).ToListAsync(token);
        return rows.Select(row => new BookFacetResponse(row["_id"].AsString, row["count"].ToInt64())).ToArray();
    }

    private async Task<PagedResult<BookCatalogEntry>> SearchByPopularityAsync(
        FilterDefinition<Book> filter,
        NormalizedBookQuery query,
        string? viewerUsername,
        long total,
        CancellationToken token)
    {
        var renderedFilter = filter.Render(new RenderArgs<Book>(_books.DocumentSerializer, _books.Settings.SerializerRegistry));
        var direction = query.Direction == "asc" ? 1 : -1;
        var pipeline = new[]
        {
            new BsonDocument("$match", renderedFilter),
            new BsonDocument("$lookup", new BsonDocument
            {
                { "from", _loans.CollectionNamespace.CollectionName },
                { "let", new BsonDocument("bookId", new BsonDocument("$toString", "$_id")) },
                { "pipeline", new BsonArray
                    {
                        new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$BookId", "$$bookId" }))),
                        new BsonDocument("$count", "value")
                    }
                },
                { "as", "_reservationCounts" }
            }),
            new BsonDocument("$set", new BsonDocument("_reservationCount", new BsonDocument("$ifNull", new BsonArray
            {
                new BsonDocument("$first", "$_reservationCounts.value"),
                0
            }))),
            new BsonDocument("$sort", new BsonDocument { { "_reservationCount", direction }, { "_id", direction } }),
            new BsonDocument("$skip", ((long)query.Page - 1) * query.PageSize),
            new BsonDocument("$limit", query.PageSize)
        };
        var documents = await _books.Aggregate<BsonDocument>(pipeline).ToListAsync(token);
        var entries = documents.Select(document =>
        {
            var count = document["_reservationCount"].ToInt64();
            document.Remove("_reservationCount");
            document.Remove("_reservationCounts");
            return new BookCatalogEntry(BsonSerializer.Deserialize<Book>(document), count, false);
        }).ToArray();
        var favoriteIds = await FavoriteBookIdsAsync(viewerUsername, entries.Select(entry => entry.Book.Id), token);
        return new(entries.Select(entry => entry with { IsFavorite = favoriteIds.Contains(entry.Book.Id) }).ToArray(), query.Page, query.PageSize, total);
    }

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
