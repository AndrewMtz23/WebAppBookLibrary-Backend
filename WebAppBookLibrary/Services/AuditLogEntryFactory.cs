using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public static class AuditLogEntryFactory
{
    private static readonly HashSet<string> AllowedMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "field", "operation", "status", "mediaType", "role", "reasonCode", "count"
    };
    public static LogEntry Create(
        string level,
        string message,
        Exception? exception,
        HttpContext? context)
    {
        return new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = SanitizeMessage(message, exception),
            Exception = exception?.GetType().Name,
            Username = context?.User.Identity?.Name,
            Controller = context?.Request.RouteValues["controller"]?.ToString(),
            Action = context?.Request.RouteValues["action"]?.ToString(),
            Method = context?.Request.Method,
            IP = context?.Connection.RemoteIpAddress?.ToString(),
            CorrelationId = context?.TraceIdentifier
        };
    }

    public static LogEntry BookChanged(string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata, HttpContext? context) =>
        DomainChanged("book", action, actorId, targetId, metadata, context);

    public static LogEntry LoanChanged(string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata, HttpContext? context) =>
        DomainChanged("loan", action, actorId, targetId, metadata, context);

    public static LogEntry UserChanged(string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata, HttpContext? context) =>
        DomainChanged("user", action, actorId, targetId, metadata, context);

    public static LogEntry AuthenticationObserved(string action, string actorId, IReadOnlyDictionary<string, string>? metadata, HttpContext? context) =>
        DomainChanged("authentication", action, actorId, actorId, metadata, context);

    private static LogEntry DomainChanged(string aggregate, string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata, HttpContext? context)
    {
        var entry = Create("INFORMATION", $"{aggregate}.{action}", null, context);
        entry.EventType = $"{aggregate}.{action}";
        entry.ActorId = actorId;
        entry.TargetId = targetId;
        entry.Metadata = metadata?.Where(item => AllowedMetadata.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase) ?? [];
        return entry;
    }

    private static string SanitizeMessage(string message, Exception? exception)
    {
        if (exception is null || string.IsNullOrEmpty(exception.Message))
            return message;

        var sanitized = message.Replace(exception.ToString(), "[redacted]", StringComparison.Ordinal);
        return sanitized.Replace(exception.Message, "[redacted]", StringComparison.Ordinal);
    }
}
