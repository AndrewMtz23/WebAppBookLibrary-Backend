using MongoDB.Bson;
using MongoDB.Driver;
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

    public async Task<Book?> ReserveAvailableBookAsync(string bookId)
    {
        if (!ObjectId.TryParse(bookId, out _))
            return null;

        var filter = Builders<Book>.Filter.Where(book =>
            book.Id == bookId && book.IsAvailable);
        var update = Builders<Book>.Update.Set(book => book.IsAvailable, false);
        var options = new FindOneAndUpdateOptions<Book, Book>
        {
            ReturnDocument = ReturnDocument.Before
        };

        return await _books.FindOneAndUpdateAsync(filter, update, options);
    }

    public async Task RestoreBookAvailabilityAsync(string bookId)
    {
        if (!ObjectId.TryParse(bookId, out _))
            return;

        var filter = Builders<Book>.Filter.Where(book =>
            book.Id == bookId && !book.IsAvailable);
        var update = Builders<Book>.Update.Set(book => book.IsAvailable, true);
        await _books.UpdateOneAsync(filter, update);
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
}
