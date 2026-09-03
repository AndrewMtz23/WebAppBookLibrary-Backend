using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;

namespace WebAppBookLibrary.Services;

public class LoanService
{
    private const string Error = "ERROR";
    private const string Warning = "WARNING";

    private readonly ILoanStore _loanStore;
    private readonly IMongoCollection<Loan> _loans = null!;
    private readonly IMongoCollection<Book> _books = null!;
    private readonly IMongoCollection<User> _users = null!;
    private readonly Logservice _logService = null!;

    public LoanService(ILoanStore loanStore)
    {
        _loanStore = loanStore;
    }

    public LoanService(
        ILoanStore loanStore,
        MongoDBService dbService,
        Logservice logService)
        : this(loanStore)
    {
        _loans = dbService.Loans;
        _books = dbService.Books;
        _users = dbService.Users;
        _logService = logService;
    }

    public async Task<LoanOperationResult> CreateLoanAsync(string bookId, string username)
    {
        Book? reservedBook = null;

        try
        {
            var user = await _loanStore.FindActiveUserAsync(username);
            if (user is null)
                return Failure(LoanOperationErrorCodes.InvalidUser);

            reservedBook = await _loanStore.ReserveAvailableBookAsync(bookId);
            if (reservedBook is null)
                return Failure(LoanOperationErrorCodes.BookUnavailable);

            var loan = new Loan
            {
                BookId = bookId,
                UserId = user.Id,
                LoanDate = DateTime.UtcNow,
                IsReturned = false
            };

            await _loanStore.InsertLoanAsync(loan);
            return new LoanOperationResult(true, string.Empty, loan);
        }
        catch
        {
            if (reservedBook is not null)
            {
                try
                {
                    await _loanStore.RestoreBookAvailabilityAsync(bookId);
                }
                catch
                {
                    // The insertion error remains the primary operation result.
                }
            }

            return Failure(LoanOperationErrorCodes.LoanPersistenceFailed);
        }
    }

    public async Task<List<object>> GetLoansByUsernameAsync(string username)
    {
        try
        {
            var user = await _users.Find(u => u.Username == username).FirstOrDefaultAsync();
            if (user is null)
                return [];

            var loans = await _loans.Find(l => l.UserId == user.Id).ToListAsync();
            var result = new List<object>();

            foreach (var loan in loans)
            {
                var book = await _books.Find(b => b.Id == loan.BookId).FirstOrDefaultAsync();
                var dueDate = loan.LoanDate.AddDays(14);

                var status = "active";
                if (loan.IsReturned)
                    status = "returned";
                else if (DateTime.UtcNow > dueDate)
                    status = "overdue";

                result.Add(new
                {
                    id = loan.Id,
                    bookTitle = book?.Title ?? "N/A",
                    loanDate = loan.LoanDate,
                    dueDate,
                    returnDate = loan.ReturnDate,
                    status
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            await _logService.LogAsync(Error, $"Error fetching loans for user {username}.", ex);
            return [];
        }
    }

    public async Task<List<Loan>> GetAllLoansAsync()
    {
        try
        {
            return await _loans.Find(_ => true).ToListAsync();
        }
        catch (Exception ex)
        {
            await _logService.LogAsync(Error, "Error fetching all loans.", ex);
            return [];
        }
    }

    public async Task<List<object>> GetAllLoansWithDetailsAsync()
    {
        try
        {
            var loans = await _loans.Find(_ => true).ToListAsync();
            var result = new List<object>();

            foreach (var loan in loans)
            {
                var book = await _books.Find(b => b.Id == loan.BookId).FirstOrDefaultAsync();
                var user = await _users.Find(u => u.Id == loan.UserId).FirstOrDefaultAsync();
                var dueDate = loan.LoanDate.AddDays(14);

                var status = "active";
                if (loan.IsReturned)
                    status = "returned";
                else if (DateTime.UtcNow > dueDate)
                    status = "overdue";

                result.Add(new
                {
                    id = loan.Id,
                    bookId = loan.BookId,
                    bookTitle = book?.Title ?? "N/A",
                    bookAuthor = book?.Author ?? "N/A",
                    userId = loan.UserId,
                    username = user?.Username ?? "N/A",
                    userEmail = user?.Email ?? "N/A",
                    loanDate = loan.LoanDate,
                    dueDate,
                    returnDate = loan.ReturnDate,
                    status,
                    isOverdue = DateTime.UtcNow > dueDate && !loan.IsReturned
                });
            }

            return result.OrderByDescending(x => ((dynamic)x).loanDate).ToList();
        }
        catch (Exception ex)
        {
            await _logService.LogAsync(Error, "Error fetching all loans with details.", ex);
            return [];
        }
    }

    public async Task<LoanOperationResult> MarkAsReturnedAsync(
        string loanId,
        string username,
        string callerRole)
    {
        if (!IsCanonicalRole(callerRole))
            return Failure(LoanOperationErrorCodes.Forbidden);

        try
        {
            var user = await _loanStore.FindActiveUserAsync(username);
            if (user is null)
                return Failure(LoanOperationErrorCodes.InvalidUser);

            var loan = await _loanStore.FindActiveLoanAsync(loanId);
            if (loan is null)
                return await ResolveMissingActiveLoanAsync(loanId, user, callerRole);

            if (!CanReturn(loan, user, callerRole))
                return Failure(LoanOperationErrorCodes.Forbidden);

            var returnedAtUtc = DateTime.UtcNow;
            var marked = await _loanStore.MarkReturnedAsync(loanId, returnedAtUtc);
            if (!marked)
                return await ResolveMissingActiveLoanAsync(loanId, user, callerRole);

            await _loanStore.RestoreBookAvailabilityAsync(loan.BookId);

            loan.IsReturned = true;
            loan.ReturnDate = returnedAtUtc;
            return new LoanOperationResult(true, string.Empty, loan);
        }
        catch
        {
            return Failure(LoanOperationErrorCodes.LoanPersistenceFailed);
        }
    }

    public async Task<(bool Success, string Message)> DeleteLoanAsync(string loanId)
    {
        try
        {
            var objectId = ObjectId.Parse(loanId);
            var loan = await _loans.Find(l => l.Id == objectId.ToString()).FirstOrDefaultAsync();
            if (loan is null)
                return (false, "Loan not found.");

            if (!loan.IsReturned)
            {
                var update = Builders<Book>.Update.Set(b => b.IsAvailable, true);
                await _books.UpdateOneAsync(b => b.Id == loan.BookId, update);
            }

            await _loans.DeleteOneAsync(l => l.Id == objectId.ToString());
            await _logService.LogAsync(Warning, $"Loan {loanId} deleted.");
            return (true, "Loan deleted successfully.");
        }
        catch (Exception ex)
        {
            await _logService.LogAsync(Error, $"Error deleting loan {loanId}.", ex);
            return (false, "Error deleting loan.");
        }
    }

    private async Task<LoanOperationResult> ResolveMissingActiveLoanAsync(
        string loanId,
        User user,
        string callerRole)
    {
        var loan = await _loanStore.FindLoanAsync(loanId);
        if (loan is null || !loan.IsReturned)
            return Failure(LoanOperationErrorCodes.LoanNotFound);

        if (!CanReturn(loan, user, callerRole))
            return Failure(LoanOperationErrorCodes.Forbidden);

        return new LoanOperationResult(true, string.Empty, loan, Idempotent: true);
    }

    private static bool CanReturn(Loan loan, User user, string callerRole)
    {
        return callerRole switch
        {
            RoleNames.User => loan.UserId == user.Id,
            RoleNames.Librarian or RoleNames.Admin => true,
            _ => false
        };
    }

    private static bool IsCanonicalRole(string callerRole)
    {
        return callerRole is RoleNames.User or RoleNames.Librarian or RoleNames.Admin;
    }

    private static LoanOperationResult Failure(string errorCode)
    {
        return new LoanOperationResult(false, errorCode);
    }
}
