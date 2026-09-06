using WebAppBookLibrary.Models;

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
            entry.EventType,
            entry.ActorId,
            entry.ActorUsername,
            entry.TargetType,
            entry.TargetId,
            entry.CorrelationId,
            entry.Metadata);
    }

    private static string SanitizeLegacyMessage(LogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Exception))
            return entry.Message;

        var firstLine = entry.Exception
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrEmpty(firstLine))
            return entry.Message;

        var separatorIndex = firstLine.IndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex < 0)
            return entry.Message;

        var exceptionMessage = firstLine[(separatorIndex + 2)..];
        return entry.Message.Replace(exceptionMessage, "[redacted]", StringComparison.Ordinal);
    }

    private static string? MaskIp(string? value)
    {
        if (!System.Net.IPAddress.TryParse(value, out var address)) return null;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) bytes[3] = 0;
        else for (var index = 8; index < bytes.Length; index++) bytes[index] = 0;
        return new System.Net.IPAddress(bytes).ToString();
    }
}
