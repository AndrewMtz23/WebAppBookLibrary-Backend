using WebAppBookLibrary.Domain.Loans;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Models;
using System.ComponentModel.DataAnnotations;

namespace WebAppBookLibrary.Contracts.Loans;

public sealed record LoanResponse(string Id, string BookId, string UserId, string MediaType, string Status, DateTime ReservedAt, DateTime? DueAt, DateTime? ReturnedAt, DateTime? CancelledAt, string? Notes, string? BookTitle = null, string? Username = null, string? DisplayName = null)
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

public sealed class LoanQuery : IValidatableObject
{
    public const int MaxQueryLength = 200;
    public string? Query { get; set; }
    public string? Status { get; set; }
    public string? MediaType { get; set; }
    public string? UserId { get; set; }
    public string? BookId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public DateTime? DueFrom { get; set; }
    public DateTime? DueTo { get; set; }
    public string DateField { get; set; } = "reservedAt";
    public string Sort { get; set; } = "reservedAt";
    public string Direction { get; set; } = "desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public NormalizedLoanQuery Normalize()
    {
        var status = Status?.Trim().ToLowerInvariant();
        if (status is not (LoanStatuses.Active or LoanStatuses.Overdue or LoanStatuses.Returned or LoanStatuses.Cancelled or "outstanding")) status = null;
        var media = MediaType?.Trim().ToLowerInvariant();
        if (media is not (MediaTypes.Physical or MediaTypes.Digital)) media = null;
        var text = Query?.Trim(); if (string.IsNullOrEmpty(text)) text = null; else text = text[..Math.Min(text.Length, MaxQueryLength)];
        var dateField = DateField is "returnedAt" or "cancelledAt" ? DateField : "reservedAt";
        var sort = Sort == "dueAt" ? "dueAt" : "reservedAt";
        return new(text, status, media, UserId?.Trim(), BookId?.Trim(), From?.ToUniversalTime(), To?.ToUniversalTime(), DueFrom?.ToUniversalTime(), DueTo?.ToUniversalTime(), dateField, sort, string.Equals(Direction, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc", Math.Max(1, Page), Math.Clamp(PageSize, 1, 100), true);
    }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From > To) yield return new("from must be before to.", [nameof(To)]);
        if (DueFrom > DueTo) yield return new("dueFrom must be before dueTo.", [nameof(DueTo)]);
    }
}
public sealed record NormalizedLoanQuery(string? Query, string? Status, string? MediaType, string? UserId, string? BookId, DateTime? From, DateTime? To, DateTime? DueFrom, DateTime? DueTo, string DateField, string Sort, string Direction, int Page, int PageSize, bool IncludeIdTieBreaker);
