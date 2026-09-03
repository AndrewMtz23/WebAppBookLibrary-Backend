using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public interface ILoanStore
{
    Task<Book?> ReserveAvailableBookAsync(string bookId, string loanId);

    Task<bool> RestoreBookAvailabilityAsync(
        string bookId,
        string loanId,
        bool allowLegacyUncorrelated);

    Task<User?> FindActiveUserAsync(string username);

    Task InsertLoanAsync(Loan loan);

    Task<Loan?> FindActiveLoanAsync(string loanId);

    Task<Loan?> FindLoanAsync(string loanId);

    Task<bool> HasActiveLoanForBookAsync(string bookId, string excludingLoanId);

    Task<bool> MarkReturnedAsync(string loanId, DateTime returnedAtUtc);

    Task<bool> DeleteLoanAsync(string loanId);
}
