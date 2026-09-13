using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Contracts.Audit;

public sealed record AuditLogResponse(
    string Id,
    DateTime Timestamp,
    string Level,
    string Message,
    string? Username,
    string? Action,
    string? Controller,
    string? IP,
    string? Method,
    int? StatusCode,
    string? EventType,
    string? ActorId,
    string? ActorUsername,
    string? TargetType,
    string? TargetId,
    string? CorrelationId,
    IReadOnlyDictionary<string, string> Metadata)
{
    public static AuditLogResponse From(LogEntry entry)
    {
        return new AuditLogResponse(
            entry.Id,
            entry.Timestamp,
            entry.Level,
            SanitizeLegacyMessage(entry),
            entry.Username,
            entry.Action,
            entry.Controller,
            MaskIp(entry.IP),
            entry.Method,
            entry.StatusCode,
            entry.EventType,
            entry.ActorId,
            entry.ActorUsername,
            entry.TargetType,
            entry.TargetId,
            entry.CorrelationId,
            AuditLogEntryFactory.SafeMetadata(entry.Metadata));
    }

    internal static string SanitizeLegacyMessage(LogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Exception))
        {
            var bounded = entry.Message.Length > 500 ? entry.Message[..500] : entry.Message;
            bounded = System.Text.RegularExpressions.Regex.Replace(bounded, @"(?i)(password|token|authorization)\s*[:=]\s*[^\s,;]+", "$1=[redacted]");
            return System.Text.RegularExpressions.Regex.Replace(bounded, @"https?://[^\s]+", "[redacted-url]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        var prefixEnd = entry.Message.IndexOf(':');
        if (prefixEnd is > 0 and <= 100) return $"{entry.Message[..prefixEnd]}: [redacted]";
        return "[redacted]";
    }

    public static AuditLogDetailResponse Detail(LogEntry entry)
    {
        var exceptionType = string.IsNullOrWhiteSpace(entry.Exception) ? null : entry.Exception.Split([':', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (exceptionType is not null && (exceptionType.Length > 120 || exceptionType.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_' or '`')))) exceptionType = "RecordedException";
        return new AuditLogDetailResponse(From(entry), exceptionType, exceptionType is null ? null : "Se registró una excepción; el detalle sensible fue omitido.");
    }

    public static string? MaskIp(string? value)
    {
        if (!System.Net.IPAddress.TryParse(value, out var address)) return null;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) bytes[3] = 0;
        else for (var index = 8; index < bytes.Length; index++) bytes[index] = 0;
        return new System.Net.IPAddress(bytes).ToString();
    }
}

public sealed record AuditLogDetailResponse(AuditLogResponse Log, string? ExceptionType, string? ExceptionSummary);
