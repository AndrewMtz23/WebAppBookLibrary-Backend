using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
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

        [HttpGet]
        public async Task<IActionResult> Search([FromQuery] AuditLogQuery query, CancellationToken token)
        {
            if (!query.TryNormalize(out var normalized, out var error)) return ApiProblemFactory.Result(400, error);
            var value = normalized!;
            var builder = Builders<LogEntry>.Filter;
            var filters = new List<FilterDefinition<LogEntry>>();
            if (value.Query is not null)
            {
                var regex = new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(value.Query), "i");
                filters.Add(builder.Or(builder.Regex(item => item.EventType, regex), builder.Regex(item => item.ActorUsername, regex),
                    builder.Regex(item => item.Controller, regex), builder.Regex(item => item.TargetId, regex), builder.Regex(item => item.CorrelationId, regex)));
            }
            if (value.Level is not null) filters.Add(builder.Eq(item => item.Level, value.Level));
            if (value.EventType == "destructive") filters.Add(builder.In(item => item.EventType, new[] { "book.permanently_deleted", "book.deactivated", "loan.cancelled" }));
            else if (value.EventType is not null) filters.Add(builder.Eq(item => item.EventType, value.EventType));
            if (value.Actor is not null) filters.Add(builder.Or(builder.Eq(item => item.ActorUsername, value.Actor), builder.Eq(item => item.ActorId, value.Actor), builder.Eq(item => item.Username, value.Actor)));
            if (value.Controller is not null) filters.Add(builder.Eq(item => item.Controller, value.Controller));
            if (value.TargetId is not null) filters.Add(builder.Eq(item => item.TargetId, value.TargetId));
            if (value.CorrelationId is not null) filters.Add(builder.Eq(item => item.CorrelationId, value.CorrelationId));
            if (value.StatusCodeFrom is not null) filters.Add(builder.Gte(item => item.StatusCode, value.StatusCodeFrom));
            if (value.StatusCodeTo is not null) filters.Add(builder.Lt(item => item.StatusCode, value.StatusCodeTo));
            if (value.From is not null) filters.Add(builder.Gte(item => item.Timestamp, value.From.Value));
            if (value.To is not null) filters.Add(builder.Lt(item => item.Timestamp, value.To.Value));
            var filter = filters.Count == 0 ? builder.Empty : builder.And(filters);
            var total = await _logs.CountDocumentsAsync(filter, cancellationToken: token);
            var skip = ((long)value.Page - 1) * value.PageSize;
            List<LogEntry> rows;
            if (skip >= total) rows = [];
            else if (skip <= int.MaxValue) rows = await _logs.Find(filter).SortByDescending(item => item.Timestamp).ThenByDescending(item => item.Id)
                .Skip((int)skip).Limit(value.PageSize).ToListAsync(token);
            else rows = await _logs.Aggregate().Match(filter)
                .Sort(Builders<LogEntry>.Sort.Descending(item => item.Timestamp).Descending(item => item.Id))
                .Skip(skip).Limit(value.PageSize).ToListAsync(token);
            return Ok(new AuditLogPage(rows.Select(AuditLogResponse.From).ToArray(), value.Page, value.PageSize, total));
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Detail(string id, CancellationToken token)
        {
            if (!ObjectId.TryParse(id, out var objectId)) return ApiProblemFactory.Result(400, "invalid_identifier");
            var canonical = objectId.ToString();
            var row = await _logs.Find(item => item.Id == canonical).FirstOrDefaultAsync(token);
            return row is null ? ApiProblemFactory.Result(404, "Audit event not found") : Ok(AuditLogResponse.Detail(row));
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
