using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Diagnostics;

namespace WebAppBookLibrary.Services;

public sealed class OperationalMetricsMiddleware(RequestDelegate next)
{
    private static readonly Meter Meter = new("BookLibrary.Http", "1.0.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("booklibrary.http.request.duration", "s", "Request latency by route template and status.");

    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var status = 500;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
            return Task.CompletedTask;
        });
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        finally
        {
            // Never label with raw paths, query strings, identities or exception messages.
            var endpoint = context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
            var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                new("http.route", route), new("http.request.method", context.Request.Method), new("http.response.status_code", status));
        }
    }
}
