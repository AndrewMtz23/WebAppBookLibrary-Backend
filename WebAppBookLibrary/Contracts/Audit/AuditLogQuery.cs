namespace WebAppBookLibrary.Contracts.Audit;

public sealed class AuditLogQuery
{
    public string? Query { get; init; }
    public string? Level { get; init; }
    public string? EventType { get; init; }
    public string? Actor { get; init; }
    public string? Controller { get; init; }
    public string? TargetId { get; init; }
    public string? CorrelationId { get; init; }
    public string? StatusCode { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;

    public bool TryNormalize(out NormalizedAuditLogQuery? value, out string error)
    {
        value = null;
        error = string.Empty;
        var query = Trim(Query, 100);
        var level = Trim(Level, 20)?.ToUpperInvariant() switch
        {
            "INFO" => "INFORMATION", "WARN" => "WARNING",
            "INFORMATION" => "INFORMATION", "WARNING" => "WARNING", "ERROR" => "ERROR", "DEBUG" => "DEBUG",
            null => null, _ => "invalid"
        };
        if (level == "invalid") { error = "invalid_level"; return false; }
        if (From is { } from && To is { } to && from >= to) { error = "invalid_range"; return false; }
        if (From is { } start && To is { } end && end - start > TimeSpan.FromDays(366)) { error = "range_too_large"; return false; }
        var statusText = Trim(StatusCode, 3);
        int? statusFrom = null;
        int? statusTo = null;
        if (statusText is not null)
        {
            if (statusText.Length == 3 && statusText[1..].Equals("xx", StringComparison.OrdinalIgnoreCase) && statusText[0] is >= '1' and <= '5')
            {
                statusFrom = (statusText[0] - '0') * 100;
                statusTo = statusFrom + 100;
            }
            else if (int.TryParse(statusText, out var exact) && exact is >= 100 and <= 599) { statusFrom = exact; statusTo = exact + 1; }
            else { error = "invalid_status_code"; return false; }
        }
        value = new(query, level, Trim(EventType, 100), Trim(Actor, 100), Trim(Controller, 100), Trim(TargetId, 100),
            Trim(CorrelationId, 100), statusFrom, statusTo, Utc(From), Utc(To), Math.Max(1, Page), Math.Clamp(PageSize, 1, 100));
        return true;
    }

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static DateTime? Utc(DateTime? value) => value is null ? null : value.Value.Kind == DateTimeKind.Utc ? value : value.Value.ToUniversalTime();
}

public sealed record NormalizedAuditLogQuery(string? Query, string? Level, string? EventType, string? Actor, string? Controller,
    string? TargetId, string? CorrelationId, int? StatusCodeFrom, int? StatusCodeTo, DateTime? From, DateTime? To, int Page, int PageSize);

public sealed record AuditLogPage(IReadOnlyList<AuditLogResponse> Items, int Page, int PageSize, long TotalItems);
