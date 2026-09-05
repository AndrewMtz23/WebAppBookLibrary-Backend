using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Loans;
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

    public async Task<Book?> FindActiveBookAsync(string bookId, CancellationToken token) =>
        await _books.Find(book => book.Id == bookId && book.IsActive).FirstOrDefaultAsync(token);

    public async Task<bool> HasActiveReservationAsync(string userId, string bookId, CancellationToken token)
    {
        var key = ActiveKey(userId, bookId);
        return await _loans.Find(loan => loan.ActiveReservationKey == key).AnyAsync(token);
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
        var filter = Builders<Loan>.Filter.Eq(loan => loan.Id, loanId) & Builders<Loan>.Filter.In(loan => loan.Status, allowedStatuses);
        var update = Builders<Loan>.Update.Set(loan => loan.Status, nextStatus).Set(loan => loan.ActiveReservationKey, null);
        update = nextStatus == LoanStatuses.Returned
            ? update.Set(loan => loan.ReturnedAt, changedAtUtc).Set(loan => loan.ReturnDate, changedAtUtc).Set(loan => loan.IsReturned, true)
            : update.Set(loan => loan.CancelledAt, changedAtUtc);
        return (await _loans.UpdateOneAsync(filter, update, cancellationToken: token)).ModifiedCount == 1;
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
