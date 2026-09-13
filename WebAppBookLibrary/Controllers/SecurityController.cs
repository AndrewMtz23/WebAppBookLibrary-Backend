using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Audit;
using WebAppBookLibrary.Contracts.Dashboard;
using WebAppBookLibrary.Contracts.Security;
using WebAppBookLibrary.Errors;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Controllers;

[ApiController]
[Route("api/security")]
[Authorize(Policy = PolicyNames.ViewSecurity)]
public sealed class SecurityController(MongoDBService database) : ControllerBase
{
    private readonly IMongoCollection<LogEntry> _logs = database.LogEntries;

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] DashboardQuery query, CancellationToken token)
    {
        if (!DashboardPeriod.TryCreate(query.From, query.To, query.Timezone, out var period, out var error)) return ApiProblemFactory.Result(400, error);
        var p = period!;
        var range = Builders<LogEntry>.Filter.Gte(item => item.Timestamp, p.FromUtc) & Builders<LogEntry>.Filter.Lt(item => item.Timestamp, p.ToUtc);
        Task<long> Count(FilterDefinition<LogEntry> filter) => _logs.CountDocumentsAsync(range & filter, cancellationToken: token);
        var authenticationFailures = await Count(Builders<LogEntry>.Filter.Eq(item => item.EventType, "authentication.failed"));
        var requestEvent = Builders<LogEntry>.Filter.Eq(item => item.EventType, "request.observed");
        var unauthorized = await Count(requestEvent & Builders<LogEntry>.Filter.Eq(item => item.StatusCode, 401));
        var forbidden = await Count(requestEvent & Builders<LogEntry>.Filter.Eq(item => item.StatusCode, 403));
        var conflicts = await Count(requestEvent & Builders<LogEntry>.Filter.Eq(item => item.StatusCode, 409));
        var serverErrors = await Count(requestEvent & Builders<LogEntry>.Filter.Gte(item => item.StatusCode, 500) & Builders<LogEntry>.Filter.Lt(item => item.StatusCode, 600));
        var roleChanges = await Count(Builders<LogEntry>.Filter.Eq(item => item.EventType, "user.role_change_attempt"));
        var statusChanges = await Count(Builders<LogEntry>.Filter.Eq(item => item.EventType, "user.status_change_attempt"));
        var destructive = await Count(Builders<LogEntry>.Filter.In(item => item.EventType, new[] { "book.permanently_deleted", "book.deactivated", "loan.cancelled" }));
        var newest = await _logs.Find(range & Builders<LogEntry>.Filter.Ne(item => item.EventType, null)).SortByDescending(item => item.Timestamp).ThenByDescending(item => item.Id).FirstOrDefaultAsync(token);
        var anomalies = await LoginAnomalies(p, token);
        return Ok(new SecuritySummaryResponse(p.GeneratedAt, p.FromUtc, p.ToUtc, p.Timezone, anomalies.Count + serverErrors,
            authenticationFailures, unauthorized, forbidden, conflicts, serverErrors, roleChanges, statusChanges, destructive,
            newest is null ? null : new SecurityNewestEvent(newest.Id, newest.EventType!, newest.Timestamp), anomalies,
            new SecurityHealth("available", DateTime.UtcNow)));
    }

    private async Task<IReadOnlyList<LoginAnomaly>> LoginAnomalies(DashboardPeriod period, CancellationToken token)
    {
        var collection = _logs.CollectionNamespace.CollectionName;
        var pipeline = new BsonDocument[]
        {
            new("$match", new BsonDocument { { "EventType", "authentication.failed" }, { "IP", new BsonDocument("$type", "string") }, { "Timestamp", new BsonDocument { { "$gte", period.FromUtc }, { "$lt", period.ToUtc } } } }),
            new("$lookup", new BsonDocument
            {
                { "from", collection }, { "let", new BsonDocument { { "origin", "$IP" }, { "windowEnd", "$Timestamp" } } },
                { "pipeline", new BsonArray
                    {
                        new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                        {
                            new BsonDocument("$eq", new BsonArray { "$EventType", "authentication.failed" }),
                            new BsonDocument("$eq", new BsonArray { "$IP", "$$origin" }),
                            new BsonDocument("$gte", new BsonArray { "$Timestamp", new BsonDocument("$dateSubtract", new BsonDocument { { "startDate", "$$windowEnd" }, { "unit", "minute" }, { "amount", 15 } }) }),
                            new BsonDocument("$lte", new BsonArray { "$Timestamp", "$$windowEnd" }),
                            new BsonDocument("$gte", new BsonArray { "$Timestamp", period.FromUtc }),
                            new BsonDocument("$lt", new BsonArray { "$Timestamp", period.ToUtc })
                        }))), new BsonDocument("$count", "count")
                    } }, { "as", "window" }
            }),
            new("$set", new BsonDocument("count", new BsonDocument("$ifNull", new BsonArray { new BsonDocument("$first", "$window.count"), 0 }))),
            new("$match", new BsonDocument("count", new BsonDocument("$gte", 10))),
            new("$sort", new BsonDocument { { "count", -1 }, { "Timestamp", -1 }, { "IP", 1 } }),
            new("$group", new BsonDocument { { "_id", "$IP" }, { "count", new BsonDocument("$first", "$count") }, { "windowEnd", new BsonDocument("$first", "$Timestamp") } }),
            new("$sort", new BsonDocument { { "count", -1 }, { "_id", 1 } }), new("$limit", 5)
        };
        var rows = await _logs.Aggregate<BsonDocument>(pipeline).ToListAsync(token);
        return rows.Select(row => new LoginAnomaly(AuditLogResponse.MaskIp(row["_id"].AsString) ?? "origen no disponible",
            row["count"].ToInt64(), row["windowEnd"].ToUniversalTime().AddMinutes(-15), row["windowEnd"].ToUniversalTime(), 10, 15)).ToArray();
    }
}
