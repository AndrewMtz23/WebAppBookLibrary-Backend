using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers
{
    [ApiController]
    [Route("[controller]")]
    [Authorize(Roles = "admin, Admin")]
    public class LogController : ControllerBase
    {
        private readonly IMongoCollection<LogEntry> _logs;

        public LogController(MongoDBService mongoDBService)
        {
            _logs = mongoDBService.LogEntries;
        }

        [HttpGet("recent")]
        public async Task<ActionResult<IEnumerable<LogEntry>>> GetRecentLogs()
        {
            var logs = await _logs
                .Find(_ => true)
                .SortByDescending(log => log.Timestamp)
                .Limit(100)
                .ToListAsync();

            return Ok(new { message = "Últimos 100 logs", data = logs });
        }

        [HttpGet("count/{level}")]
        public async Task<ActionResult<object>> GetLogCountByLevel(string level)
        {
            if (string.IsNullOrEmpty(level))
                return BadRequest(new { error = "Se requiere el nivel de log" });

            var count = await _logs
                .CountDocumentsAsync(log => log.Level.ToUpper().Equals(level.ToUpper(), StringComparison.OrdinalIgnoreCase));

            return Ok(new { level = level.ToUpper(), count });
        }
    }
}