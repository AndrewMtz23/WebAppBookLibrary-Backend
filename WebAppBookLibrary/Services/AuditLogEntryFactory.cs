using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public static class AuditLogEntryFactory
{
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
            IP = context?.Connection.RemoteIpAddress?.ToString()
        };
    }

    private static string SanitizeMessage(string message, Exception? exception)
    {
        if (exception is null || string.IsNullOrEmpty(exception.Message))
            return message;

        var sanitized = message.Replace(exception.ToString(), "[redacted]", StringComparison.Ordinal);
        return sanitized.Replace(exception.Message, "[redacted]", StringComparison.Ordinal);
    }
}
