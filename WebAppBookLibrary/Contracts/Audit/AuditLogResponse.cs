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
    string? Method)
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
            entry.IP,
            entry.Method);
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
}
