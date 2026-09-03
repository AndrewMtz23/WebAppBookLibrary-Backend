using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Contracts.Loans;

public sealed record LoanOperationResult(
    bool Success,
    string ErrorCode,
    Loan? Loan = null,
    bool Idempotent = false);

public static class LoanOperationErrorCodes
{
    public const string InvalidUser = "invalid_user";
    public const string BookUnavailable = "book_unavailable";
    public const string LoanPersistenceFailed = "loan_persistence_failed";
    public const string LoanNotFound = "loan_not_found";
    public const string Forbidden = "forbidden";
}
