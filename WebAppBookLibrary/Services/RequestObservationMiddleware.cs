using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Services;

public sealed class RequestObservationMiddleware(RequestDelegate next, ILogger<RequestObservationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, MongoDBService database)
    {
        await next(context);
        // Readiness must finish even when MongoDB is unavailable; never persist its failure into MongoDB.
        if (context.Request.Path.StartsWithSegments("/api/health")) return;
        var status = context.Response.StatusCode;
        if (status is not (401 or 403 or 409) && status < 500) return;
        try
        {
            var entry = AuditLogEntryFactory.Create(status >= 500 ? "ERROR" : "WARNING", "HTTP request completed with an operational status.", null, context);
            entry.EventType = "request.observed";
            entry.StatusCode = status;
            entry.ActorUsername = context.User.Identity?.Name;
            entry.Metadata = AuditLogEntryFactory.SafeMetadata(new Dictionary<string, string> { ["statusCode"] = status.ToString() });
            await database.LogEntries.InsertOneAsync(entry, cancellationToken: context.RequestAborted);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Audit observation could not be persisted");
        }
    }
}
