using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Loans;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class MongoLoanStore : ILoanStore
{
    private readonly IMongoCollection<Book> _books;
    private readonly IMongoCollection<Loan> _loans;
    private readonly IMongoCollection<User> _users;

    public MongoLoanStore(MongoDBService mongoDBService)
        : this(mongoDBService.Books, mongoDBService.Loans, mongoDBService.Users)
    {
    }

    public MongoLoanStore(
        IMongoCollection<Book> books,
        IMongoCollection<Loan> loans,
        IMongoCollection<User> users)
    {
        _books = books;
        _loans = loans;
        _users = users;
    }

    public async Task<Book?> FindActiveBookAsync(string bookId, CancellationToken token)
    {
        var active = Builders<Book>.Filter.Eq(book => book.IsActive, true) | Builders<Book>.Filter.Exists(book => book.IsActive, false);
        return await _books.Find(Builders<Book>.Filter.Eq(book => book.Id, bookId) & active).FirstOrDefaultAsync(token);
    }

    public async Task<bool> HasActiveReservationAsync(string userId, string bookId, CancellationToken token)
    {
        var key = ActiveKey(userId, bookId);
        var builder = Builders<Loan>.Filter;
        var activeState = builder.In(loan => loan.Status, [LoanStatuses.Active, LoanStatuses.Overdue]) |
                          (builder.Exists(loan => loan.Status, false) & builder.Eq(loan => loan.IsReturned, false));
        var filter = builder.Eq(loan => loan.ActiveReservationKey, key) |
                     (builder.Eq(loan => loan.UserId, userId) & builder.Eq(loan => loan.BookId, bookId) & activeState);
        return await _loans.Find(filter).AnyAsync(token);
    }

    public async Task<bool> TryDecrementPhysicalInventoryAsync(string bookId, DateTime updatedAtUtc, CancellationToken token)
    {
        var filter = Builders<Book>.Filter.Where(book => book.Id == bookId && book.IsActive && book.MediaType == MediaTypes.Physical && book.AvailableCopies > 0);
        var update = Builders<Book>.Update.Inc(book => book.AvailableCopies, -1).Set(book => book.UpdatedAt, updatedAtUtc);
        return (await _books.UpdateOneAsync(filter, update, cancellationToken: token)).ModifiedCount == 1;
    }

    public async Task<bool> TryIncrementPhysicalInventoryAsync(string bookId, DateTime updatedAtUtc, CancellationToken token)
    {
        var filter = Builders<Book>.Filter.Where(book => book.Id == bookId && book.MediaType == MediaTypes.Physical && book.AvailableCopies < book.TotalCopies);
        var update = Builders<Book>.Update.Inc(book => book.AvailableCopies, 1).Set(book => book.UpdatedAt, updatedAtUtc);
        return (await _books.UpdateOneAsync(filter, update, cancellationToken: token)).ModifiedCount == 1;
    }

    public async Task InsertLoanAsync(Loan loan, CancellationToken token)
    {
        loan.ActiveReservationKey = ActiveKey(loan.UserId, loan.BookId);
        using var session = await _books.Database.Client.StartSessionAsync(cancellationToken: token);
        await session.WithTransactionAsync(async (transaction, ct) =>
        {
            var b = Builders<Book>.Filter;
            var media = loan.MediaType == MediaTypes.Digital ? b.Eq(x => x.MediaType, MediaTypes.Digital)
                : b.Eq(x => x.MediaType, MediaTypes.Physical) | b.Exists(x => x.MediaType, false);
            var active = b.Eq(x => x.IsActive, true) | b.Exists(x => x.IsActive, false);
            var changed = await _books.UpdateOneAsync(transaction, b.Eq(x => x.Id, loan.BookId) & active & media,
                Builders<Book>.Update.Inc(x => x.ReferenceVersion, 1), cancellationToken: ct);
            if (changed.MatchedCount != 1) throw new BookReferenceUnavailableException();
            await _loans.InsertOneAsync(transaction, loan, cancellationToken: ct);
            return true;
        }, cancellationToken: token);
    }

    public async Task<Loan?> FindLoanAsync(string loanId, CancellationToken token) =>
        await _loans.Find(loan => loan.Id == loanId).FirstOrDefaultAsync(token);

    public async Task<bool> TransitionAsync(string loanId, IReadOnlyCollection<string> allowedStatuses, string nextStatus, DateTime changedAtUtc, CancellationToken token)
    {
        var builder = Builders<Loan>.Filter;
        var activeState = builder.In(loan => loan.Status, allowedStatuses) |
                          (builder.Exists(loan => loan.Status, false) & builder.Eq(loan => loan.IsReturned, false));
        var filter = builder.Eq(loan => loan.Id, loanId) & activeState;
        var update = Builders<Loan>.Update.Set(loan => loan.Status, nextStatus).Set(loan => loan.ActiveReservationKey, null);
        update = nextStatus == LoanStatuses.Returned
            ? update.Set(loan => loan.ReturnedAt, changedAtUtc).Set(loan => loan.ReturnDate, changedAtUtc).Set(loan => loan.IsReturned, true)
            : update.Set(loan => loan.CancelledAt, changedAtUtc);
        return (await _loans.UpdateOneAsync(filter, update, cancellationToken: token)).ModifiedCount == 1;
    }

    public async Task<bool> CompletePhysicalAsync(string loanId, string bookId, string nextStatus, DateTime changedAtUtc, CancellationToken token)
    {
        using var session = await _loans.Database.Client.StartSessionAsync(cancellationToken: token);
        session.StartTransaction();
        try
        {
            var builder = Builders<Loan>.Filter;
            var activeState = builder.In(loan => loan.Status, [LoanStatuses.Active, LoanStatuses.Overdue]) |
                              (builder.Exists(loan => loan.Status, false) & builder.Eq(loan => loan.IsReturned, false));
            var loanFilter = builder.Eq(loan => loan.Id, loanId) & activeState;
            var loanUpdate = Builders<Loan>.Update.Set(loan => loan.Status, nextStatus).Set(loan => loan.ActiveReservationKey, null);
            loanUpdate = nextStatus == LoanStatuses.Returned
                ? loanUpdate.Set(loan => loan.ReturnedAt, changedAtUtc).Set(loan => loan.ReturnDate, changedAtUtc).Set(loan => loan.IsReturned, true)
                : loanUpdate.Set(loan => loan.CancelledAt, changedAtUtc);
            var loanResult = await _loans.UpdateOneAsync(session, loanFilter, loanUpdate, cancellationToken: token);
            if (loanResult.ModifiedCount != 1) { await session.AbortTransactionAsync(token); return false; }

            var currentBook = await _books.Find(session, book => book.Id == bookId).FirstOrDefaultAsync(token);
            if (currentBook is null) { await session.AbortTransactionAsync(token); return false; }
            var bookFilter = Builders<Book>.Filter.Eq(book => book.Id, bookId);
            UpdateDefinition<Book> bookUpdate;
            if (currentBook.TotalCopies is null || currentBook.AvailableCopies is null)
            {
                bookFilter &= Builders<Book>.Filter.Or(Builders<Book>.Filter.Eq(book => book.ActiveLoanId, loanId), Builders<Book>.Filter.Exists(book => book.ActiveLoanId, false));
                bookUpdate = Builders<Book>.Update.Set(book => book.TotalCopies, 1).Set(book => book.AvailableCopies, 1).Set(book => book.IsAvailable, true).Set(book => book.ActiveLoanId, null).Set(book => book.UpdatedAt, changedAtUtc);
            }
            else
            {
                var capacityFilter = new BsonDocument("$expr", new BsonDocument("$lt", new BsonArray { "$AvailableCopies", "$TotalCopies" }));
                bookFilter &= capacityFilter;
                bookUpdate = Builders<Book>.Update.Inc(book => book.AvailableCopies, 1).Set(book => book.IsAvailable, true).Set(book => book.UpdatedAt, changedAtUtc);
            }
            var bookResult = await _books.UpdateOneAsync(session, bookFilter, bookUpdate, cancellationToken: token);
            if (bookResult.ModifiedCount != 1) { await session.AbortTransactionAsync(token); return false; }
            await session.CommitTransactionAsync(token);
            return true;
        }
        catch
        {
            if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None);
            return false;
        }
    }

    public async Task<PagedResult<Loan>> SearchAsync(NormalizedLoanQuery query, CancellationToken token)
    {
        // Compute the same read-time values as LoanResponse.From before filtering,
        // sorting and counting. Keep persisted fields untouched for deserialization.
        var pipeline = new List<BsonDocument>
        {
            BsonDocument.Parse("""
            { "$set": {
                "_legacyMedia": { "$regexMatch": { "input": { "$ifNull": ["$MediaType", ""] }, "regex": "^\\s*$" } },
                "_legacyStatus": { "$regexMatch": { "input": { "$ifNull": ["$Status", ""] }, "regex": "^\\s*$" } },
                "_reservedAt": { "$cond": [
                    { "$or": [ { "$eq": [{ "$ifNull": ["$ReservedAt", null] }, null] }, { "$eq": ["$ReservedAt", { "$date": "0001-01-01T00:00:00Z" }] } ] },
                    { "$ifNull": ["$LoanDate", "$$NOW"] }, "$ReservedAt"] },
                "_returnedAt": { "$ifNull": ["$ReturnedAt", "$ReturnDate"] }
            } }
            """),
            BsonDocument.Parse("""
            { "$set": {
                "_mediaType": { "$cond": ["$_legacyMedia", "physical", "$MediaType"] },
                "_dueAt": { "$ifNull": ["$DueAt", { "$cond": ["$_legacyMedia", { "$add": [{ "$ifNull": ["$LoanDate", "$$NOW"] }, 1209600000] }, null] }] }
            } }
            """),
            BsonDocument.Parse("""
            { "$set": { "_status": { "$cond": ["$_legacyStatus",
                { "$cond": [{ "$eq": ["$IsReturned", true] }, "returned",
                    { "$cond": [{ "$and": [{ "$ne": ["$_dueAt", null] }, { "$lt": ["$_dueAt", "$$NOW"] }] }, "overdue", "active"] }] },
                { "$cond": [{ "$and": [{ "$eq": ["$Status", "active"] }, { "$ne": [{ "$ifNull": ["$DueAt", null] }, null] }, { "$lt": ["$DueAt", "$$NOW"] }] }, "overdue", "$Status"] }
            ] } } }
            """)
        };
        var filters = new BsonArray();
        if (query.Status == "outstanding") filters.Add(new BsonDocument("_status", new BsonDocument("$in", new BsonArray { "active", "overdue" })));
        else if (query.Status is not null) filters.Add(new BsonDocument("_status", query.Status));
        if (query.MediaType is not null) filters.Add(new BsonDocument("_mediaType", query.MediaType));
        if (!string.IsNullOrWhiteSpace(query.UserId)) filters.Add(new BsonDocument("UserId", query.UserId));
        if (!string.IsNullOrWhiteSpace(query.BookId)) filters.Add(new BsonDocument("BookId", query.BookId));
        var dateField = query.DateField switch { "returnedAt" => "_returnedAt", "cancelledAt" => "CancelledAt", _ => "_reservedAt" };
        if (query.From is not null) filters.Add(new BsonDocument(dateField, new BsonDocument("$gte", query.From.Value)));
        if (query.To is not null) filters.Add(new BsonDocument(dateField, new BsonDocument("$lt", query.To.Value)));
        if (query.DueFrom is not null) filters.Add(new BsonDocument("_dueAt", new BsonDocument("$gte", query.DueFrom.Value)));
        if (query.DueTo is not null) filters.Add(new BsonDocument("_dueAt", new BsonDocument("$lt", query.DueTo.Value)));
        if (filters.Count > 0) pipeline.Add(new BsonDocument("$match", new BsonDocument("$and", filters)));
        if (query.Query is not null)
        {
            var escaped = System.Text.RegularExpressions.Regex.Escape(query.Query);
            var regex = new BsonRegularExpression(escaped, "i");
            pipeline.Add(Lookup(_books.CollectionNamespace.CollectionName, "BookId", "_book", new BsonDocument("Title", 1)));
            pipeline.Add(Lookup(_users.CollectionNamespace.CollectionName, "UserId", "_user", new BsonDocument { { "Username", 1 }, { "DisplayName", 1 } }));
            pipeline.Add(new BsonDocument("$match", new BsonDocument("$or", new BsonArray
            {
                new BsonDocument("_book.Title", regex), new BsonDocument("_user.Username", regex), new BsonDocument("_user.DisplayName", regex),
                new BsonDocument("$expr", new BsonDocument("$regexMatch", new BsonDocument { { "input", new BsonDocument("$toString", "$_id") }, { "regex", escaped }, { "options", "i" } }))
            })));
        }
        var direction = query.Direction == "asc" ? 1 : -1;
        var sortField = query.Sort == "dueAt" ? "_dueAt" : "_reservedAt";
        pipeline.Add(new BsonDocument("$facet", new BsonDocument
        {
            { "metadata", new BsonArray { new BsonDocument("$count", "total") } },
            { "items", new BsonArray {
                new BsonDocument("$sort", new BsonDocument { { sortField, direction }, { "_id", direction } }),
                new BsonDocument("$skip", ((long)query.Page - 1) * query.PageSize),
                new BsonDocument("$limit", query.PageSize),
                new BsonDocument("$unset", new BsonArray { "_book", "_user", "_legacyMedia", "_legacyStatus", "_reservedAt", "_returnedAt", "_mediaType", "_dueAt", "_status" }) }
            }
        }));
        var result = await _loans.Aggregate<BsonDocument>(pipeline).FirstOrDefaultAsync(token);
        var total = result?["metadata"].AsBsonArray.FirstOrDefault()?.AsBsonDocument.GetValue("total", 0).ToInt64() ?? 0;
        var items = result is null ? [] : result["items"].AsBsonArray.Select(value => BsonSerializer.Deserialize<Loan>(value.AsBsonDocument)).ToArray();
        return new(items, query.Page, query.PageSize, total);
    }

    private static BsonDocument Lookup(string collection, string localIdField, string output, BsonDocument projection) =>
        new("$lookup", new BsonDocument
        {
            { "from", collection }, { "let", new BsonDocument("localId", "$" + localIdField) },
            { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { new BsonDocument("$toString", "$_id"), "$$localId" }))), new BsonDocument("$project", projection) } },
            { "as", output }
        });

    public async Task<PagedResult<LoanSearchEntry>> SearchDetailsAsync(NormalizedLoanQuery query, CancellationToken token)
    {
        var page = await SearchAsync(query, token);
        var bookIds = page.Items.Select(item => item.BookId).Distinct().ToArray();
        var userIds = page.Items.Select(item => item.UserId).Distinct().ToArray();
        var books = await _books.Find(Builders<Book>.Filter.In(book => book.Id, bookIds)).Project(book => new { book.Id, book.Title }).ToListAsync(token);
        var users = await _users.Find(Builders<User>.Filter.In(user => user.Id, userIds)).Project(user => new { user.Id, user.Username, user.DisplayName }).ToListAsync(token);
        var bookNames = books.ToDictionary(item => item.Id, item => item.Title);
        var userNames = users.ToDictionary(item => item.Id);
        var entries = page.Items.Select(loan => userNames.TryGetValue(loan.UserId, out var user)
            ? new LoanSearchEntry(loan, bookNames.GetValueOrDefault(loan.BookId), user.Username, user.DisplayName)
            : new LoanSearchEntry(loan, bookNames.GetValueOrDefault(loan.BookId), null, null)).ToArray();
        return new(entries, page.Page, page.PageSize, page.TotalItems);
    }

    public async Task<Book?> ReserveAvailableBookAsync(string bookId, string loanId)
    {
        if (!ObjectId.TryParse(bookId, out _) || !ObjectId.TryParse(loanId, out _))
            return null;

        var filter = Builders<Book>.Filter.Where(book =>
            book.Id == bookId && book.IsAvailable && book.ActiveLoanId == null);
        var update = Builders<Book>.Update
            .Set(book => book.IsAvailable, false)
            .Set(book => book.ActiveLoanId, loanId);
        var options = new FindOneAndUpdateOptions<Book, Book>
        {
            ReturnDocument = ReturnDocument.Before
        };

        return await _books.FindOneAndUpdateAsync(filter, update, options);
    }

    public async Task<bool> RestoreBookAvailabilityAsync(
        string bookId,
        string loanId,
        bool allowLegacyUncorrelated)
    {
        if (!ObjectId.TryParse(bookId, out _) || !ObjectId.TryParse(loanId, out _))
            return false;

        var filter = Builders<Book>.Filter.Where(book =>
            book.Id == bookId &&
            (book.ActiveLoanId == loanId ||
             (book.ActiveLoanId == null &&
              (allowLegacyUncorrelated || book.IsAvailable))));
        var update = Builders<Book>.Update
            .Set(book => book.IsAvailable, true)
            .Set(book => book.ActiveLoanId, null);
        var result = await _books.UpdateOneAsync(filter, update);

        return result.MatchedCount == 1;
    }

    public async Task<User?> FindActiveUserAsync(string username)
    {
        return await _users.Find(user =>
            user.Username == username && user.IsActive).FirstOrDefaultAsync();
    }

    public Task InsertLoanAsync(Loan loan)
    {
        if (!ObjectId.TryParse(loan.BookId, out _) ||
            !ObjectId.TryParse(loan.UserId, out _) ||
            (!string.IsNullOrEmpty(loan.Id) && !ObjectId.TryParse(loan.Id, out _)))
        {
            throw new ArgumentException("Loan identifiers must be valid ObjectIds.", nameof(loan));
        }

        return InsertLoanAsync(loan, CancellationToken.None);
    }

    public async Task<Loan?> FindActiveLoanAsync(string loanId)
    {
        if (!ObjectId.TryParse(loanId, out _))
            return null;

        return await _loans.Find(loan =>
            loan.Id == loanId && !loan.IsReturned).FirstOrDefaultAsync();
    }

    public async Task<Loan?> FindLoanAsync(string loanId)
    {
        if (!ObjectId.TryParse(loanId, out _))
            return null;

        return await _loans.Find(loan => loan.Id == loanId).FirstOrDefaultAsync();
    }

    public async Task<bool> HasActiveLoanForBookAsync(string bookId, string excludingLoanId)
    {
        if (!ObjectId.TryParse(bookId, out _) || !ObjectId.TryParse(excludingLoanId, out _))
            return false;

        var filter = Builders<Loan>.Filter.Where(loan =>
            loan.BookId == bookId &&
            loan.Id != excludingLoanId &&
            loan.IsReturned == false);
        var count = await _loans.CountDocumentsAsync(
            filter,
            new CountOptions { Limit = 1 });

        return count > 0;
    }

    public async Task<bool> MarkReturnedAsync(string loanId, DateTime returnedAtUtc)
    {
        if (!ObjectId.TryParse(loanId, out _))
            return false;

        var filter = Builders<Loan>.Filter.Where(loan =>
            loan.Id == loanId && loan.IsReturned == false);
        var update = Builders<Loan>.Update
            .Set(loan => loan.IsReturned, true)
            .Set(loan => loan.ReturnDate, returnedAtUtc);
        var result = await _loans.UpdateOneAsync(filter, update);

        return result.ModifiedCount == 1;
    }

    public async Task<bool> DeleteLoanAsync(string loanId)
    {
        if (!ObjectId.TryParse(loanId, out _))
            return false;

        var result = await _loans.DeleteOneAsync(loan => loan.Id == loanId);
        return result.DeletedCount == 1;
    }

    private static string ActiveKey(string userId, string bookId) => $"{userId}:{bookId}";
}
