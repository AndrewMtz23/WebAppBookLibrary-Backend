namespace WebAppBookLibrary.Contracts.Circulation;

public sealed record CirculationPolicyResponse(
    string Mode,
    int PickupHours,
    int LoanDays,
    int RenewalDays,
    int MaxRenewals,
    string PolicyVersion = "circulation-v1"
);

public sealed record JoinWaitlistRequest(
    string BookId, string? IdempotencyKey = null
);

public sealed record WaitlistEntryResponse(
    string Id,
    string BookId,
    string? BookTitle,
    string? BookAuthor,
    string? BookCoverUrl,
    string Status,
    DateTime CreatedAt,
    int? QueuePosition,
    DateTime? OfferExpiresAt,
    string? PickupReservationId
);

public sealed record MyWaitlistResponse(
    IReadOnlyList<WaitlistEntryResponse> Items,
    int TotalCount
);

public sealed record ReservePickupRequest(
    string BookId,
    string? IdempotencyKey = null
);

public sealed record PickupReservationResponse(
    string Id,
    string UserId,
    string? UserDisplayName,
    string BookId,
    string? BookTitle,
    string? BookAuthor,
    string? BookCoverUrl,
    string Status,
    DateTime ReadyAt,
    DateTime PickupExpiresAt,
    DateTime? CollectedAt,
    DateTime? CancelledAt,
    string? CancelledReason,
    string? LoanId,
    long Version
);

public sealed record CollectPickupRequest(
    long ExpectedVersion
);

public sealed record CancelPickupRequest(
    string? Reason,
    long ExpectedVersion
);

public sealed record PickupReservationListPage(
    IReadOnlyList<PickupReservationResponse> Items,
    int TotalCount,
    int Page,
    int PageSize
);

public sealed record CreateRenewalRequest(
    string? Reason, string? IdempotencyKey = null
);

public sealed record RenewalRequestResponse(
    string Id,
    string LoanId,
    string UserId,
    string? UserDisplayName,
    string BookId,
    string? BookTitle,
    string? BookAuthor,
    string? BookCoverUrl,
    string Status,
    DateTime RequestedAt,
    DateTime OriginalDueAt,
    DateTime RequestedDueAt,
    string? Reason,
    DateTime? DecidedAt,
    string? DecidedByUserId,
    string? DecisionReason,
    long Version
);

public sealed record DecideRenewalRequest(
    bool Approve,
    string? DecisionReason,
    long ExpectedVersion
);

public sealed record RenewalRequestListPage(
    IReadOnlyList<RenewalRequestResponse> Items,
    int TotalCount,
    int Page,
    int PageSize
);
