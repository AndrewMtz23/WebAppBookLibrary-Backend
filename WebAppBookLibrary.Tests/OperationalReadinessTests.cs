using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MongoDB.Driver;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class OperationalReadinessTests
{
    [Fact]
    public async Task Handled_controller_exception_keeps_original_route_and_correlation()
    {
        var measurements = new List<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) => { if (instrument.Meter.Name == "BookLibrary.Http") current.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) => measurements.AddRange(tags.ToArray()));
        listener.Start();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseMiddleware<OperationalMetricsMiddleware>();
        app.UseExceptionHandler(handler => handler.Run(context => WebAppBookLibrary.Errors.ApiProblemFactory.WriteAsync(context, 500, "Internal server error")));
        app.UseRouting();
        app.MapGet("/failure/{id}", (RequestDelegate)(_ => throw new InvalidOperationException("never-expose")));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await client.GetAsync("/failure/private-id");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("X-Correlation-ID").Single(), await response.Content.ReadAsStringAsync());
        Assert.Contains(measurements, pair => pair.Key == "http.route" && Equals(pair.Value, "/failure/{id}"));
    }

    [LocalMongoFact]
    public Task Real_readiness_pings_isolated_database() => StaffReviewRegressionTests.WithDatabase(async database =>
        Assert.True(await new MongoReadinessProbe(new MongoDBService(database)).IsReadyAsync(default)));

    [Fact]
    public async Task Missing_dependency_fails_readiness_within_its_deadline()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://127.0.0.1:1");
        settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(200);
        var probe = new MongoReadinessProbe(new MongoDBService(new MongoClient(settings).GetDatabase("booklibrary_unreachable_test")));
        Assert.False(await probe.IsReadyAsync(default));
    }

    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 503)]
    public async Task Readiness_reflects_dependency_state_without_leaking_details(bool ready, int expected)
    {
        var probe = new Mock<IReadinessProbe>();
        probe.Setup(x => x.IsReadyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ready);
        var result = Assert.IsType<ObjectResult>(await new HealthController().Ready(probe.Object, CancellationToken.None));
        Assert.Equal(expected, result.StatusCode);
        Assert.DoesNotContain("mongodb", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.OrdinalIgnoreCase);
        Assert.IsType<OkObjectResult>(new HealthController().Get());
    }

    [Fact]
    public async Task Production_auth_policy_rejects_sixth_attempt_with_safe_problem_details()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        typeof(Program).GetMethod("ConfigureRateLimiting", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [builder.Services]);
        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.MapPost("/auth-probe", () => Results.Unauthorized()).RequireRateLimiting("auth");
        app.MapGet("/live", () => Results.Ok());
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/auth-probe", null)).StatusCode);
        var response = await client.PostAsync("/auth-probe", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("traceId", body);
        Assert.DoesNotContain("Exception", body);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/live")).StatusCode);
    }

    [Fact]
    public async Task Metrics_use_route_templates_and_measure_failed_requests_without_personal_data()
    {
        var recorded = new List<KeyValuePair<string, object?>>();
        double elapsed = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) => { if (instrument.Meter.Name == "BookLibrary.Http") current.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) => { elapsed = value; recorded.AddRange(tags.ToArray()); });
        listener.Start();
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/books/private-id";
        context.Request.QueryString = new QueryString("?email=private@example.invalid");
        context.Request.Method = "GET";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("api/books/{id}"), 0, EndpointMetadataCollection.Empty, "Book detail"));
        var middleware = new OperationalMetricsMiddleware(_ => throw new InvalidOperationException("private-value"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));
        Assert.True(elapsed >= 0);
        Assert.Contains(recorded, pair => pair.Key == "http.route" && Equals(pair.Value, "api/books/{id}"));
        Assert.Contains(recorded, pair => pair.Key == "http.response.status_code" && Equals(pair.Value, 500));
        Assert.DoesNotContain(recorded, pair => pair.Value?.ToString()?.Contains("private") == true);
    }
}
