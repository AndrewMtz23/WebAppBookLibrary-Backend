using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Support;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Services.Support;

namespace WebAppBookLibrary.Tests;
public sealed class SupportHttpTests
{
    [LocalMongoFact]
    public Task Http_contract_rejects_unauthorized_invalid_and_conflicting_requests() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", o => {
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(o => o.AddPolicy("ManageSupport", p => p.RequireRole("admin", "librarian")));
        builder.Services.AddControllers().AddApplicationPart(typeof(NotificationsController).Assembly);
        builder.Services.AddSingleton(f.Db); builder.Services.AddSingleton<TimeProvider>(new SupportPersistenceTests.Clock());
        builder.Services.Configure<SupportOptions>(_ => {}); builder.Services.AddScoped<MongoSupportStore>(); builder.Services.AddScoped<SupportQueries>();
        await using var app = builder.Build();
        app.Use(async (context, next) => {
            if (context.Request.Headers.TryGetValue("Test-User", out var id)) {
                var user = await f.Db.Users.Find(u => u.Id == id.ToString()).FirstOrDefaultAsync();
                if (user != null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Role, user.Role)], "test"));
            }
            await next(context);
        });
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/support/tickets/my")).StatusCode);
        client.DefaultRequestHeaders.Add("Test-User", f.User.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/support/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/support/tickets/my?pageSize=101")).StatusCode);
        var created = await client.PostAsJsonAsync("/api/support/tickets", SupportPersistenceTests.Request());
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var ticket = (await created.Content.ReadFromJsonAsync<SupportTicketDetail>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/replies", new AddSupportMessage("hello", 1))).StatusCode);
        var conflict = await client.PostAsJsonAsync($"/api/support/tickets/{ticket.Id}/replies", new AddSupportMessage("again", 1));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType!.MediaType);
        client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", admin.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/support/tickets/{ticket.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/support/tickets/{ticket.Id}/staff")).StatusCode);
    });
}
