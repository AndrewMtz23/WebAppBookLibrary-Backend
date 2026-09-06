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
            var logEntry = AuditLogEntryFactory.Create(
                level,
                message,
                exception,
                _httpContextAccessor.HttpContext);

            await _logs.InsertOneAsync(logEntry);

            LogToProvider(level, message, exception);
        }

        public Task LogDomainAsync(LogEntry entry) => _logs.InsertOneAsync(entry);
        public Task BookChangedAsync(string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata = null) => LogDomainAsync(AuditLogEntryFactory.BookChanged(action, actorId, targetId, metadata, _httpContextAccessor.HttpContext));
        public Task LoanChangedAsync(string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata = null) => LogDomainAsync(AuditLogEntryFactory.LoanChanged(action, actorId, targetId, metadata, _httpContextAccessor.HttpContext));
        public Task UserChangedAsync(string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata = null) => LogDomainAsync(AuditLogEntryFactory.UserChanged(action, actorId, targetId, metadata, _httpContextAccessor.HttpContext));
        public Task AuthenticationObservedAsync(string action, string actorId, IReadOnlyDictionary<string, string>? metadata = null) => LogDomainAsync(AuditLogEntryFactory.AuthenticationObserved(action, actorId, metadata, _httpContextAccessor.HttpContext));

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
