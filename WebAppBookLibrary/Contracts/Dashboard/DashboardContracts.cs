namespace WebAppBookLibrary.Contracts.Dashboard;

public sealed record MetricCount(string Key, long Count);
public sealed record DashboardComparison(long Current, long Previous, decimal? PercentageChange)
{
    public static DashboardComparison From(long current, long previous) =>
        new(current, previous, previous == 0 ? null : decimal.Round((current - previous) * 100m / previous, 2));
}
public sealed record DashboardRankItem(string Id, string Label, long Count);
public sealed record DashboardSeriesPoint(DateOnly Date, long Physical, long Digital);
public sealed record DashboardBookAttention(string Id, string Title, long AvailableCopies);
public sealed record DashboardActivityItem(string Id, string EventType, DateTime Timestamp, string? ActorUsername, string? TargetType, string? TargetId);

public sealed record ReaderDashboardResponse(DateTime GeneratedAt, DateTime From, DateTime To, long TotalReservations, long Favorites, IReadOnlyList<MetricCount> ByMedia);

public sealed record LibrarianDashboardResponse(
    DateTime GeneratedAt, DateTime From, DateTime To, long TotalReservations, long ActiveReservations,
    long OverdueReservations, long ActiveBooks, long AvailablePhysicalCopies, IReadOnlyList<MetricCount> ByMedia,
    string Timezone, DateTime PreviousFrom, DateTime PreviousTo, DashboardComparison ReturnedPhysical,
    DashboardComparison DigitalReservations, long LowInventoryTitles, long OutOfStockTitles,
    IReadOnlyList<DashboardRankItem> TopReservedTitles, IReadOnlyList<DashboardRankItem> TitlesWithoutReservations);

public sealed record AdminDashboardResponse(
    DateTime GeneratedAt, DateTime From, DateTime To, long ActiveUsers, long TotalBooks, long TotalReservations,
    IReadOnlyList<MetricCount> UsersByRole, IReadOnlyList<MetricCount> ReservationsByMedia,
    string Timezone, DateTime PreviousFrom, DateTime PreviousTo, long ActiveTitles, long OutstandingReservations,
    long OverdueReservations, DashboardComparison PeriodReservations, IReadOnlyList<DashboardSeriesPoint> DailyReservations,
    IReadOnlyList<MetricCount> ActiveTitlesByGenre, IReadOnlyList<MetricCount> ActiveTitlesByMedia,
    IReadOnlyList<DashboardRankItem> TopReservedTitles, long InactiveAccounts, long InventoryAttentionTitles,
    IReadOnlyList<DashboardBookAttention> InventoryAttention, long LowInventoryTitles, long OutOfStockTitles);

public sealed record DashboardPeriod(
    DateTime FromUtc, DateTime ToUtc, string Timezone, DateTime GeneratedAt,
    DateTime PreviousFromUtc, DateTime PreviousToUtc, int LocalCalendarDays)
{
    public static bool TryCreate(DateOnly? from, DateOnly? to, string? timezone, out DashboardPeriod? period, out string error)
    {
        period = null;
        error = string.Empty;
        var zoneId = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim();
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (TimeZoneNotFoundException) { error = "invalid_timezone"; return false; }
        catch (InvalidTimeZoneException) { error = "invalid_timezone"; return false; }
        if (!zone.HasIanaId && !string.Equals(zoneId, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            error = "invalid_timezone";
            return false;
        }
        try
        {
            var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
            var end = to ?? localToday;
            var start = from ?? end.AddDays(-29);
            if (start > end) { error = "invalid_range"; return false; }
            var days = end.DayNumber - start.DayNumber + 1;
            if (days > 366) { error = "range_too_large"; return false; }
            var fromLocal = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var toLocal = end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var previousFromLocal = start.AddDays(-days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(fromLocal) || zone.IsInvalidTime(toLocal) || zone.IsInvalidTime(previousFromLocal))
            {
                error = "invalid_local_time";
                return false;
            }
            var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromLocal, zone);
            var toUtc = TimeZoneInfo.ConvertTimeToUtc(toLocal, zone);
            period = new(fromUtc, toUtc, zoneId, DateTime.UtcNow, TimeZoneInfo.ConvertTimeToUtc(previousFromLocal, zone), fromUtc, days);
            return true;
        }
        catch (ArgumentOutOfRangeException) { error = "date_overflow"; return false; }
        catch (ArgumentException) { error = "invalid_local_time"; return false; }
    }
}

public sealed class DashboardQuery
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string? Timezone { get; init; }
}
