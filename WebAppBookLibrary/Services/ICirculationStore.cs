using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Circulation;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed record WaitlistJoinResult(
    bool Success,
    string? ErrorCode = null,
    WaitlistEntry? Entry = null,
    int? QueuePosition = null,
    bool ImmediateOffer = false,
    PickupReservation? OfferedReservation = null
);

public sealed record ReservePickupResult(
    bool Success,
    string? ErrorCode = null,
    PickupReservation? Reservation = null,
    bool Idempotent = false
);

public sealed record CollectPickupResult(
    bool Success,
    string? ErrorCode = null,
    Loan? Loan = null,
    PickupReservation? Reservation = null,
    bool Idempotent = false
);

public sealed record RenewalRequestResult(
    bool Success,
    string? ErrorCode = null,
    RenewalRequest? Request = null
);

public sealed record RenewalDecisionResult(
    bool Success,
    string? ErrorCode = null,
    RenewalRequest? Request = null,
    Loan? Loan = null
);

public interface ICirculationStore
{
    CirculationOptions Options { get; }
    Task<string> GetModeAsync(CancellationToken ct);
    Task<WebAppBookLibrary.Contracts.Loans.LoanOperationResult> ReserveLegacyAsync(string userId, string bookId, string actor, DateTime now, CancellationToken ct);

    Task<WaitlistJoinResult> JoinWaitlistAsync(string userId, string bookId, CancellationToken ct, string? idempotencyKey = null);
    Task<bool> LeaveWaitlistAsync(string userId, string waitlistEntryId, string? reason, CancellationToken ct);
    Task<MyWaitlistResponse> GetMyWaitlistAsync(string userId, int page, int pageSize, CancellationToken ct);
    Task<int> GetQueuePositionAsync(string bookId, DateTime createdAt, string entryId, CancellationToken ct);

    Task<ReservePickupResult> ReserveForPickupAsync(string userId, string bookId, string? idempotencyKey, CancellationToken ct);
    Task<PickupReservationListPage> GetMyPickupReservationsAsync(string userId, int page, int pageSize, CancellationToken ct);
    Task<PickupReservationListPage> GetStaffPickupReservationsAsync(string? status, string? search, int page, int pageSize, CancellationToken ct);
    Task<CollectPickupResult> CollectPickupReservationAsync(string pickupId, string staffUserId, long expectedVersion, CancellationToken ct);
    Task<bool> CancelPickupReservationAsync(string pickupId, string actorId, bool isStaff, string? reason, long expectedVersion, CancellationToken ct);

    Task<RenewalRequestResult> RequestRenewalAsync(string loanId, string userId, string? reason, CancellationToken ct, string? idempotencyKey = null);
    Task<RenewalRequestListPage> GetMyRenewalRequestsAsync(string userId, int page, int pageSize, CancellationToken ct);
    Task<RenewalRequestListPage> GetStaffRenewalRequestsAsync(string? status, string? search, int page, int pageSize, CancellationToken ct);
    Task<RenewalDecisionResult> DecideRenewalAsync(string renewalRequestId, string staffUserId, bool approve, string? decisionReason, long expectedVersion, CancellationToken ct);

    Task<bool> ReturnPhysicalLoanAsync(string loanId, string staffUserId, CancellationToken ct, string nextStatus = "returned");
    Task PromoteAvailableAsync(CancellationToken ct);
    Task<int> ProcessExpiredPickupReservationsAsync(DateTime now, CancellationToken ct);

    Task<IReadOnlyList<CirculationHistoryEntry>> GetHistoryAsync(string entityId, CancellationToken ct);
}
