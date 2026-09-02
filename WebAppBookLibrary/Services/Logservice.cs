using MongoDB.Driver;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services
{
    public class Logservice
    {
        private readonly IMongoCollection<LogEntry> _logs;
        private readonly ILogger<Logservice> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public Logservice(
            MongoDBService mongoDBService,
            ILogger<Logservice> logger,
            IHttpContextAccessor httpContextAccessor)
        {
            _logs = mongoDBService.LogEntries;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task LogAsync(string level, string message, Exception? exception = null)
        {
            var username = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
            var controller = _httpContextAccessor.HttpContext?.Request.RouteValues["controller"]?.ToString();
            var action = _httpContextAccessor.HttpContext?.Request.RouteValues["action"]?.ToString();
            var method = _httpContextAccessor.HttpContext?.Request.Method;
            var ip = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

            var logEntry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message,
                Exception = exception?.ToString(),
                Username = username,
                Controller = controller,
                Action = action,
                Method = method,
                IP = ip
            };

            await _logs.InsertOneAsync(logEntry);

            LogToProvider(level, message, exception);
        }

        private void LogToProvider(string level, string message, Exception? exception)
        {
            switch (level.ToUpper())
            {
                case "ERROR":
                    _logger.LogError(exception, "Error occurred: {Message}", message);
                    break;
                case "WARNING":
                    _logger.LogWarning("Warning: {Message}", message);
                    break;
                case "INFORMATION":
                    _logger.LogInformation("Information: {Message}", message);
                    break;
                default:
                    _logger.LogDebug("Debug: {Message}", message);
                    break;
            }
        }
    }
}
