namespace WebAppBookLibrary.Domain.Loans;

public static class LoanRules
{
    public static bool CanTransition(string current, string next) =>
        (current, next) switch
        {
            (LoanStatuses.Active, LoanStatuses.Returned) => true,
            (LoanStatuses.Active, LoanStatuses.Overdue) => true,
            (LoanStatuses.Active, LoanStatuses.Cancelled) => true,
            (LoanStatuses.Overdue, LoanStatuses.Returned) => true,
            (LoanStatuses.Overdue, LoanStatuses.Cancelled) => true,
            _ => false
        };

    public static string EffectiveStatus(string status, DateTime? dueAtUtc, DateTime nowUtc)
    {
        if (status == LoanStatuses.Active && dueAtUtc is not null && dueAtUtc.Value < nowUtc)
            return LoanStatuses.Overdue;

        return status;
    }
}
