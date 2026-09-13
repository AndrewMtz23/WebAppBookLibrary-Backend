namespace WebAppBookLibrary.Contracts.Security;

public sealed record SecurityHealth(string Status, DateTime CheckedAt);
public sealed record SecurityNewestEvent(string Id, string EventType, DateTime Timestamp);
public sealed record LoginAnomaly(string MaskedOrigin, long Count, DateTime WindowStart, DateTime WindowEnd, int Threshold, int WindowMinutes);
public sealed record SecuritySummaryResponse(
    DateTime GeneratedAt, DateTime From, DateTime To, string Timezone, long Alerts,
    long AuthenticationFailures, long UnauthorizedResponses, long ForbiddenResponses, long ConflictResponses, long ServerErrors,
    long RoleChanges, long StatusChanges, long DestructiveActions, SecurityNewestEvent? NewestEvent,
    IReadOnlyList<LoginAnomaly> UnusualLoginActivity, SecurityHealth ApiHealth);
