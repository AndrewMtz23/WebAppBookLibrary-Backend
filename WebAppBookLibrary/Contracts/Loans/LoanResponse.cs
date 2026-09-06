using WebAppBookLibrary.Domain.Loans;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Contracts.Loans;

public sealed record LoanResponse(string Id, string BookId, string UserId, string MediaType, string Status, DateTime ReservedAt, DateTime? DueAt, DateTime? ReturnedAt, DateTime? CancelledAt, string? Notes)
{
    public static LoanResponse From(Loan loan, DateTime nowUtc) => new(
        loan.Id, loan.BookId, loan.UserId,
        string.IsNullOrWhiteSpace(loan.MediaType) ? MediaTypes.Physical : loan.MediaType,
        EffectiveStatus(loan, nowUtc),
        loan.ReservedAt == default ? loan.LoanDate : loan.ReservedAt,
        EffectiveDueAt(loan), loan.ReturnedAt ?? loan.ReturnDate, loan.CancelledAt, loan.Notes);

    private static DateTime? EffectiveDueAt(Loan loan) => loan.DueAt ?? (string.IsNullOrWhiteSpace(loan.MediaType) ? loan.LoanDate.AddDays(14) : null);
    private static string EffectiveStatus(Loan loan, DateTime nowUtc)
    {
        if (!string.IsNullOrWhiteSpace(loan.Status)) return LoanRules.EffectiveStatus(loan.Status, loan.DueAt, nowUtc);
        if (loan.IsReturned) return LoanStatuses.Returned;
        return EffectiveDueAt(loan) < nowUtc ? LoanStatuses.Overdue : LoanStatuses.Active;
    }
}

public sealed record DigitalAccessResponse(string ResourceUrl);
public sealed record DigitalAccessResult(bool Success, string ErrorCode, string? ResourceUrl = null);

public sealed class LoanQuery
{
    public string? Status { get; init; }
    public string? MediaType { get; init; }
    public string? UserId { get; init; }
    public string? BookId { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public NormalizedLoanQuery Normalize()
    {
        var status = Status?.Trim().ToLowerInvariant();
        if (status is not (LoanStatuses.Active or LoanStatuses.Overdue or LoanStatuses.Returned or LoanStatuses.Cancelled)) status = null;
        var media = MediaType?.Trim().ToLowerInvariant();
        if (media is not (MediaTypes.Physical or MediaTypes.Digital)) media = null;
        return new(status, media, UserId?.Trim(), BookId?.Trim(), From?.ToUniversalTime(), To?.ToUniversalTime(), Math.Max(1, Page), Math.Clamp(PageSize, 1, 100));
    }
}
public sealed record NormalizedLoanQuery(string? Status, string? MediaType, string? UserId, string? BookId, DateTime? From, DateTime? To, int Page, int PageSize);
