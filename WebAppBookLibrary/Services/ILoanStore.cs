using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public interface ILoanStore
{
    Task<Book?> ReserveAvailableBookAsync(string bookId);

    Task RestoreBookAvailabilityAsync(string bookId);

    Task<User?> FindActiveUserAsync(string username);

    Task InsertLoanAsync(Loan loan);

    Task<Loan?> FindActiveLoanAsync(string loanId);

    Task<Loan?> FindLoanAsync(string loanId);

    Task<bool> MarkReturnedAsync(string loanId, DateTime returnedAtUtc);
}
