using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class CirculationHttpTests
{
    [LocalMongoFact]
    public Task Http_journey_enforces_roles_privacy_versions_and_renewal_rules() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await AdvancedCirculationTests.Seed(db); var stranger = await AdvancedCirculationTests.Reader(f.Db);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", o => { o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; }; o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; }; });
        builder.Services.AddAuthorization(); builder.Services.AddControllers().AddApplicationPart(typeof(LoansController).Assembly);
        builder.Services.AddSingleton(f.Db); builder.Services.AddSingleton<ICirculationStore>(f.Store);
        builder.Services.AddSingleton(Options.Create(new CirculationOptions { Mode = "active" }));
        await using var app = builder.Build();
        app.Use(async (context, next) => {
            if (context.Request.Headers.TryGetValue("Test-User", out var id)) {
                var account = await f.Db.Users.Find(u => u.Id == id.ToString()).FirstOrDefaultAsync();
                if (account != null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, account.Id), new(ClaimTypes.Role, context.Request.Headers["Test-Role"].ToString() == "librarian" ? "librarian" : "user")], "test"));
            }
            await next(context);
        });
        app.UseAuthorization(); app.MapControllers(); await app.StartAsync(); using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/circulation/policy")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/pickup-reservations/my")).StatusCode);
        client.DefaultRequestHeaders.Add("Test-User", f.Reader.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/pickup-reservations", new { bookId = "bad" })).StatusCode);
        var created = await client.PostAsJsonAsync("/api/pickup-reservations", new { bookId = f.Book.Id, idempotencyKey = "http-1" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pickup = await f.Db.PickupReservations.Find(p => p.UserId == f.Reader.Id).SingleAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/pickup-reservations/{pickup.Id}/collect", new { expectedVersion = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/pickup-reservations/my?pageSize=101")).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", stranger.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/pickup-reservations/{pickup.Id}/cancel", new { expectedVersion = 1 })).StatusCode);
        client.DefaultRequestHeaders.Add("Test-Role", "librarian");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/pickup-reservations/{pickup.Id}/collect", new { expectedVersion = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/pickup-reservations?search=Visible")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/pickup-reservations/{pickup.Id}/collect", new { expectedVersion = 1 })).StatusCode);
        var loan = await f.Db.Loans.Find(l => l.UserId == f.Reader.Id).SingleAsync();
        client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", f.Reader.Id); client.DefaultRequestHeaders.Remove("Test-Role");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/loans/{loan.Id}/renewal-requests", new { reason = "More time", idempotencyKey = "renewal-http" })).StatusCode);
        var request = await f.Db.RenewalRequests.Find(r => r.LoanId == loan.Id).SingleAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/renewal-requests/{request.Id}/decision", new { approve = true, expectedVersion = 1 })).StatusCode);
        client.DefaultRequestHeaders.Add("Test-Role", "librarian");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/renewal-requests/{request.Id}/decision", new { approve = true, expectedVersion = 1 })).StatusCode);
        Assert.Equal(loan.DueAt!.Value.AddDays(7), (await f.Db.Loans.Find(l => l.Id == loan.Id).SingleAsync()).DueAt);
    });
}
