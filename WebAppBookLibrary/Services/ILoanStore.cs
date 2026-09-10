using WebAppBookLibrary.Models;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Domain.Common;

namespace WebAppBookLibrary.Services;

public interface ILoanStore
{
    Task<Book?> FindActiveBookAsync(string bookId, CancellationToken cancellationToken);
    Task<bool> HasActiveReservationAsync(string userId, string bookId, CancellationToken cancellationToken);
    Task<bool> TryDecrementPhysicalInventoryAsync(string bookId, DateTime updatedAtUtc, CancellationToken cancellationToken);
    Task<bool> TryIncrementPhysicalInventoryAsync(string bookId, DateTime updatedAtUtc, CancellationToken cancellationToken);
    Task InsertLoanAsync(Loan loan, CancellationToken cancellationToken);
    Task<Loan?> FindLoanAsync(string loanId, CancellationToken cancellationToken);
    Task<bool> TransitionAsync(string loanId, IReadOnlyCollection<string> allowedStatuses, string nextStatus, DateTime changedAtUtc, CancellationToken cancellationToken);
    Task<bool> CompletePhysicalAsync(string loanId, string bookId, string nextStatus, DateTime changedAtUtc, CancellationToken cancellationToken);
    Task<PagedResult<Loan>> SearchAsync(NormalizedLoanQuery query, CancellationToken cancellationToken);
    async Task<PagedResult<LoanSearchEntry>> SearchDetailsAsync(NormalizedLoanQuery query, CancellationToken cancellationToken)
    {
        var page = await SearchAsync(query, cancellationToken);
        return new(page.Items.Select(loan => new LoanSearchEntry(loan, null, null, null)).ToArray(), page.Page, page.PageSize, page.TotalItems);
    }

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

public sealed record LoanSearchEntry(Loan Loan, string? BookTitle, string? Username, string? DisplayName);
