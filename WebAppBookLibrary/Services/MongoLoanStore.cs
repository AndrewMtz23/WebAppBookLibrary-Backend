using MongoDB.Bson;
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

    public Task InsertLoanAsync(Loan loan, CancellationToken token)
    {
        loan.ActiveReservationKey = ActiveKey(loan.UserId, loan.BookId);
        return _loans.InsertOneAsync(loan, cancellationToken: token);
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
        var builder = Builders<Loan>.Filter;
        var filters = new List<FilterDefinition<Loan>>();
        if (query.Status is not null) filters.Add(builder.Eq(loan => loan.Status, query.Status));
        if (query.MediaType is not null) filters.Add(builder.Eq(loan => loan.MediaType, query.MediaType));
        if (!string.IsNullOrWhiteSpace(query.UserId)) filters.Add(builder.Eq(loan => loan.UserId, query.UserId));
        if (!string.IsNullOrWhiteSpace(query.BookId)) filters.Add(builder.Eq(loan => loan.BookId, query.BookId));
        if (query.From is not null) filters.Add(builder.Gte(loan => loan.ReservedAt, query.From.Value));
        if (query.To is not null) filters.Add(builder.Lt(loan => loan.ReservedAt, query.To.Value));
        var filter = filters.Count == 0 ? builder.Empty : builder.And(filters);
        var total = await _loans.CountDocumentsAsync(filter, cancellationToken: token);
        var items = await _loans.Find(filter).SortByDescending(loan => loan.ReservedAt).ThenByDescending(loan => loan.Id).Skip((query.Page - 1) * query.PageSize).Limit(query.PageSize).ToListAsync(token);
        return new(items, query.Page, query.PageSize, total);
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

        return _loans.InsertOneAsync(loan);
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
