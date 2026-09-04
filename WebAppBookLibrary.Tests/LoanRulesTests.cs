using WebAppBookLibrary.Domain.Loans;

namespace WebAppBookLibrary.Tests;

public sealed class LoanRulesTests
{
    [Theory]
    [InlineData(LoanStatuses.Active, LoanStatuses.Returned, true)]
    [InlineData(LoanStatuses.Active, LoanStatuses.Cancelled, true)]
    [InlineData(LoanStatuses.Overdue, LoanStatuses.Returned, true)]
    [InlineData(LoanStatuses.Returned, LoanStatuses.Active, false)]
    [InlineData(LoanStatuses.Cancelled, LoanStatuses.Returned, false)]
    public void CanTransition_EnforcesTheLoanStateMachine(string current, string next, bool expected)
    {
        Assert.Equal(expected, LoanRules.CanTransition(current, next));
    }

    [Fact]
    public void EffectiveStatus_ProjectsAnExpiredActivePhysicalLoanAsOverdue()
    {
        var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        var result = LoanRules.EffectiveStatus(LoanStatuses.Active, now.AddMinutes(-1), now);

        Assert.Equal(LoanStatuses.Overdue, result);
    }

    [Fact]
    public void EffectiveStatus_DoesNotChangeATerminalStatus()
    {
        var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        var result = LoanRules.EffectiveStatus(LoanStatuses.Returned, now.AddDays(-1), now);

        Assert.Equal(LoanStatuses.Returned, result);
    }
}
