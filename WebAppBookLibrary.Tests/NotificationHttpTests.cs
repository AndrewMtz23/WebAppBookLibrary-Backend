using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class NotificationHttpTests
{
    [LocalMongoFact]
    public Task All_roles_only_read_their_own_inbox_and_invalid_or_inactive_requests_are_rejected() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db);
        await new MongoLoanStore(f.Db).InsertLoanAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = f.Book.MediaType, Status = "active", ReservedAt = DateTime.UtcNow }, default);
        var notice = await db.GetCollection<Notification>("Notifications").Find(FilterDefinition<Notification>.Empty).SingleAsync();
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", o => o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; });
        builder.Services.AddAuthorization(); builder.Services.AddControllers().AddApplicationPart(typeof(NotificationsController).Assembly);
        builder.Services.AddSingleton(f.Db); builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddScoped<NotificationService>();
        await using var app = builder.Build();
        app.Use(async (context, next) => {
            if (context.Request.Headers.TryGetValue("Test-User", out var id)) {
                var account = await f.Db.Users.Find(u => u.Id == id.ToString()).FirstOrDefaultAsync();
                if (account != null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, account.Id), new(ClaimTypes.Role, account.Role)], "test"));
            }
            await next(context);
        });
        app.UseAuthorization(); app.MapControllers(); await app.StartAsync(); using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/notifications/my")).StatusCode);
        foreach (var role in new[] { "user", "admin", "librarian" }) {
            var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = role, Email = role + "@example.invalid", Role = role };
            await f.Db.Users.InsertOneAsync(user); client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", user.Id);
            Assert.Empty((await client.GetFromJsonAsync<NotificationPage>("/api/notifications/my"))!.Items);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/notifications/my/{notice.Id}/read", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/notifications/my?pageSize=101")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/notifications/my?before=-1")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/notifications/my/read-all", new { through = -1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/notifications/my/preferences", new { reminders = true, email = true })).StatusCode);
            await f.Db.Users.UpdateOneAsync(u => u.Id == user.Id, Builders<User>.Update.Set(u => u.IsActive, false));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/notifications/my/preferences")).StatusCode);
        }
        client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", f.User.Id);
        var response = await client.GetAsync("/api/notifications/my"); var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("userId", json); Assert.DoesNotContain("eventKey", json); Assert.DoesNotContain("Lease", json);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/notifications/my/{notice.Id}/read", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/notifications/my/{notice.Id}/read", new { })).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<NotificationPage>("/api/notifications/my"))!.UnreadCount);
    });
}
