using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Audit;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = PolicyNames.ViewAudit)]
    public class LogController : ControllerBase
    {
        private readonly IMongoCollection<LogEntry> _logs;

        public LogController(MongoDBService mongoDBService)
        {
            _logs = mongoDBService.LogEntries;
        }

        [HttpGet("recent")]
        public async Task<IActionResult> GetRecentLogs()
        {
            var logs = await _logs
                .Find(_ => true)
                .SortByDescending(log => log.Timestamp)
                .Limit(100)
                .ToListAsync();

            return Ok(new
            {
                message = "Últimos 100 logs",
                data = logs.Select(AuditLogResponse.From)
            });
        }

        [HttpGet("count/{level}")]
        public async Task<ActionResult<object>> GetLogCountByLevel(string level)
        {
            if (string.IsNullOrEmpty(level))
                return ApiProblemFactory.Result(400, "Log level is required");

            var count = await _logs
                .CountDocumentsAsync(log => log.Level.ToUpper().Equals(level.ToUpper(), StringComparison.OrdinalIgnoreCase));

            return Ok(new { level = level.ToUpper(), count });
        }
    }
}
