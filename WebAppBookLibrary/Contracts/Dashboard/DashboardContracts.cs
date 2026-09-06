namespace WebAppBookLibrary.Contracts.Dashboard;

public sealed record MetricCount(string Key, long Count);
public sealed record ReaderDashboardResponse(DateTime GeneratedAt, DateTime From, DateTime To, long TotalReservations, long Favorites, IReadOnlyList<MetricCount> ByMedia);
public sealed record LibrarianDashboardResponse(DateTime GeneratedAt, DateTime From, DateTime To, long TotalReservations, long ActiveReservations, long OverdueReservations, long ActiveBooks, long AvailablePhysicalCopies, IReadOnlyList<MetricCount> ByMedia);
public sealed record AdminDashboardResponse(DateTime GeneratedAt, DateTime From, DateTime To, long ActiveUsers, long TotalBooks, long TotalReservations, IReadOnlyList<MetricCount> UsersByRole, IReadOnlyList<MetricCount> ReservationsByMedia);

public sealed record DashboardPeriod(DateTime FromUtc, DateTime ToUtc, string Timezone, DateTime GeneratedAt)
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
        var end = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var start = from ?? end.AddDays(-29);
        if (start > end) { error = "invalid_range"; return false; }
        if (end.DayNumber - start.DayNumber + 1 > 366) { error = "range_too_large"; return false; }
        var fromLocal = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var toLocal = end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        period = new(TimeZoneInfo.ConvertTimeToUtc(fromLocal, zone), TimeZoneInfo.ConvertTimeToUtc(toLocal, zone), zoneId, DateTime.UtcNow);
        return true;
    }
}

public sealed class DashboardQuery
{
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string? Timezone { get; init; }
}
