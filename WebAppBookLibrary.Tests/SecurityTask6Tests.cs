using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Audit;
using WebAppBookLibrary.Contracts.Dashboard;
using WebAppBookLibrary.Contracts.Security;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class SecurityTask6Tests
{
    [LocalMongoFact]
    public Task Audit_and_security_http_routes_bind_queries_and_enforce_admin_policy() => StaffReviewRegressionTests.WithDatabase(async database =>
    {
        foreach (var role in new[] { RoleNames.User, RoleNames.Librarian, RoleNames.Admin })
        {
            await using var app = await StaffHttpQueryTests.StartApp(services =>
            {
                services.AddTransient(_ => new LogController(new MongoDBService(database)));
                services.AddTransient(_ => new SecurityController(new MongoDBService(database)));
            }, role);
            using var client = StaffHttpQueryTests.Client(app);
            var expected = role == RoleNames.Admin ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
            Assert.Equal(expected, (await client.GetAsync("/api/log?page=1&pageSize=10&statusCode=5xx")).StatusCode);
            Assert.Equal(expected, (await client.GetAsync("/api/security/summary?from=2026-09-01&to=2026-09-02&timezone=UTC")).StatusCode);
            if (role == RoleNames.Admin)
            {
                Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/log?level=secret")).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/security/summary?from=2026-09-03&to=2026-09-02&timezone=UTC")).StatusCode);
            }
        }
    });

    [Fact]
    public void Audit_query_validates_bounds_level_status_and_page_size()
    {
        Assert.False(new AuditLogQuery { Level = "secret" }.TryNormalize(out _, out var levelError));
        Assert.Equal("invalid_level", levelError);
        Assert.False(new AuditLogQuery { StatusCode = "999" }.TryNormalize(out _, out var statusError));
        Assert.Equal("invalid_status_code", statusError);
        Assert.True(new AuditLogQuery { StatusCode = "5xx" }.TryNormalize(out var statusClass, out _));
        Assert.Equal(500, statusClass!.StatusCodeFrom);
        Assert.Equal(600, statusClass.StatusCodeTo);
        Assert.True(new AuditLogQuery { Query = new string('x', 120), Page = -1, PageSize = 500 }.TryNormalize(out var query, out _));
        Assert.Equal(100, query!.Query!.Length);
        Assert.Equal(1, query.Page);
        Assert.Equal(100, query.PageSize);
    }

    [LocalMongoFact]
    public Task Audit_search_detail_and_security_anomaly_reconcile_with_real_events() => StaffReviewRegressionTests.WithDatabase(async database =>
    {
        var logs = database.GetCollection<LogEntry>("LogEntries");
        var baseTime = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
        var originA = "192.0.2.41";
        var originB = "192.0.2.99";
        var observedContext = new DefaultHttpContext { TraceIdentifier = "request-corr" };
        observedContext.Request.Path = "/api/private/resource";
        observedContext.Request.QueryString = new QueryString("?password=never-store");
        observedContext.Request.Headers.Authorization = "Bearer secret-token";
        observedContext.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.27");
        var observer = new RequestObservationMiddleware(context => { context.Response.StatusCode = 409; return Task.CompletedTask; }, NullLogger<RequestObservationMiddleware>.Instance);
        await observer.InvokeAsync(observedContext, new MongoDBService(database));
        var observed = await logs.Find(item => item.CorrelationId == "request-corr").SingleAsync();
        var observedJson = observed.ToBsonDocument().ToJson();
        Assert.Equal(409, observed.StatusCode);
        Assert.DoesNotContain("never-store", observedJson);
        Assert.DoesNotContain("secret-token", observedJson);
        Assert.DoesNotContain("private/resource", observedJson);
        var failures = Enumerable.Range(0, 9).Select(index => Failed(originA, baseTime.AddMinutes(index)))
            .Concat(Enumerable.Range(0, 9).Select(index => Failed(originB, baseTime.AddMinutes(index)))).ToArray();
        await logs.InsertManyAsync(failures);
        var security = new SecurityController(new MongoDBService(database));
        var query = new DashboardQuery { From = DateOnly.FromDateTime(baseTime), To = DateOnly.FromDateTime(baseTime), Timezone = "UTC" };

        var before = Assert.IsType<OkObjectResult>(await security.Summary(query, default));
        Assert.Empty(Assert.IsType<SecuritySummaryResponse>(before.Value).UnusualLoginActivity);

        await logs.InsertOneAsync(Failed(originA, baseTime.AddMinutes(15)));
        await logs.InsertManyAsync([
            new LogEntry { Id = ObjectId.GenerateNewId().ToString(), Timestamp = baseTime.AddMinutes(16), EventType = "request.observed", StatusCode = 403, Level = "WARNING" },
            new LogEntry { Id = ObjectId.GenerateNewId().ToString(), Timestamp = baseTime.AddMinutes(17), EventType = "request.observed", StatusCode = 503, Level = "ERROR" },
            new LogEntry { Id = ObjectId.GenerateNewId().ToString(), Timestamp = baseTime.AddMinutes(17), EventType = "loan.cancelled", Level = "INFORMATION" }
        ]);
        var after = Assert.IsType<OkObjectResult>(await security.Summary(query, default));
        var summary = Assert.IsType<SecuritySummaryResponse>(after.Value);
        var anomaly = Assert.Single(summary.UnusualLoginActivity);
        Assert.Equal("192.0.2.0", anomaly.MaskedOrigin);
        Assert.Equal(10, anomaly.Count);
        Assert.Equal(10, anomaly.Threshold);
        Assert.Equal(1, summary.ForbiddenResponses);
        Assert.Equal(1, summary.ServerErrors);
        Assert.Equal(1, summary.DestructiveActions);
        Assert.Equal(2, summary.Alerts);

        var malicious = new LogEntry
        {
            Id = ObjectId.GenerateNewId().ToString(), Timestamp = baseTime.AddMinutes(18), Level = "ERROR", EventType = "book.updated",
            ActorUsername = "admin", TargetId = "book-1", CorrelationId = "corr-1", IP = "2001:db8::1234",
            Message = "Update failed: secret-password", Exception = "System.Exception: different-secret",
            Metadata = new Dictionary<string, string> { ["password"] = "never", ["result"] = "failed" }
        };
        await logs.InsertOneAsync(malicious);
        var audit = new LogController(new MongoDBService(database));
        var serverErrorResult = Assert.IsType<OkObjectResult>(await audit.Search(new AuditLogQuery { EventType = "request.observed", StatusCode = "5xx" }, default));
        Assert.Equal(summary.ServerErrors, Assert.IsType<AuditLogPage>(serverErrorResult.Value).TotalItems);
        var destructiveResult = Assert.IsType<OkObjectResult>(await audit.Search(new AuditLogQuery { EventType = "destructive" }, default));
        Assert.Equal(summary.DestructiveActions, Assert.IsType<AuditLogPage>(destructiveResult.Value).TotalItems);
        var pageResult = Assert.IsType<OkObjectResult>(await audit.Search(new AuditLogQuery { EventType = "book.updated", PageSize = 1 }, default));
        var page = Assert.IsType<AuditLogPage>(pageResult.Value);
        Assert.Equal(1, page.TotalItems);
        Assert.Equal("2001:db8::", Assert.Single(page.Items).IP);
        var detailResult = Assert.IsType<OkObjectResult>(await audit.Detail(malicious.Id, default));
        var detail = Assert.IsType<AuditLogDetailResponse>(detailResult.Value);
        Assert.Equal("Update failed: [redacted]", detail.Log.Message);
        Assert.DoesNotContain("password", detail.Log.Metadata.Keys, StringComparer.OrdinalIgnoreCase);
    });

    private static LogEntry Failed(string ip, DateTime timestamp) => new()
    {
        Id = ObjectId.GenerateNewId().ToString(), Timestamp = timestamp, Level = "WARNING", EventType = "authentication.failed",
        StatusCode = 401, IP = ip, Metadata = new Dictionary<string, string> { ["statusCode"] = "401", ["reasonCode"] = "invalid_credentials" }
    };
}
