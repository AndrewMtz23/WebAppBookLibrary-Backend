namespace WebAppBookLibrary.Domain.Loans;

public static class LoanStatuses
{
    public const string Active = "active";
    public const string Returned = "returned";
    public const string Overdue = "overdue";
    public const string Cancelled = "cancelled";

    public static bool IsCanonical(string? value) =>
        value is Active or Returned or Overdue or Cancelled;
}
