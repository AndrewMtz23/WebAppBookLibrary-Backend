namespace WebAppBookLibrary.Contracts.Loans;

public sealed record LoanTransitionResponse(string EventType, DateTime Timestamp, string? ActorUsername, string Source);
public sealed record LoanHistoryPage(IReadOnlyList<LoanTransitionResponse> Events, bool Truncated);
public sealed record LoanDetailResponse(LoanResponse Loan, IReadOnlyList<LoanTransitionResponse> History, bool HistoryTruncated);
