namespace WebAppBookLibrary.Domain.Circulation;

public static class CirculationModes
{
    public const string Legacy = "legacy";
    public const string Active = "active";
    public const string Draining = "draining";

    public static bool IsValid(string? mode) =>
        mode is Legacy or Active or Draining;
}

public static class WaitlistStatuses
{
    public const string Queued = "queued";
    public const string Offered = "offered";
    public const string Fulfilled = "fulfilled";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";

    public static readonly string[] ActiveStatuses = [Queued, Offered];
}

public static class PickupReservationStatuses
{
    public const string Ready = "ready";
    public const string Collected = "collected";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

public static class RenewalRequestStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
}
