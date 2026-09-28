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
using WebAppBookLibrary.Contracts.Reading;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class ReadingHttpTests
{
    [LocalMongoFact]
    public Task Personal_api_validates_identity_roles_revisions_and_contracts() => StaffReviewRegressionTests.WithDatabase(async database => {
        var f = await ReadingPersistenceTests.Seed(database);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", o => o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; });
        builder.Services.AddAuthorization(); builder.Services.AddControllers().AddApplicationPart(typeof(ReadingController).Assembly);
        builder.Services.AddSingleton(f.Db); builder.Services.AddSingleton<IReadingStore>(f.Store); builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddScoped<ReadingService>();
        await using var app = builder.Build();
        app.Use(async (context, next) => {
            if (context.Request.Headers.TryGetValue("Test-User", out var id)) {
                var user = await f.Db.Users.Find(u => u.Id == id.ToString()).FirstOrDefaultAsync();
                if (user is not null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.Username), new(ClaimTypes.Role, user.Role), new("credential_version", "0")], "test"));
            }
            if (!await CurrentAccountValidator.ValidateAsync(context.User, new MongoUserStore(f.Db))) { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/reading/my")).StatusCode);
        foreach (var role in new[] { "user", "librarian", "admin" }) {
            var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = role, Email = role + "@example.invalid", Role = role, IsActive = true };
            await f.Db.Users.InsertOneAsync(user);
            client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", user.Id);
            Assert.Null((await client.GetFromJsonAsync<LatestReadingResponse>("/api/reading/my/latest"))!.Entry);
            var url = "/api/reading/my/books/" + f.Book.Id;
            var saved = await client.PutAsJsonAsync(url, ReadingPersistenceTests.Request());
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var body = await saved.Content.ReadAsStringAsync(); Assert.DoesNotContain("private", body); Assert.DoesNotContain("digitalResourceUrl", body);
            var entry = (await saved.Content.ReadFromJsonAsync<ReadingResponse>())!;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(url, ReadingPersistenceTests.Request())).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { status = "reading", progressMode = "percent", progressPercent = 30, expectedRevision = entry.Revision, userId = f.User.Id })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/reading/my?pageSize=51")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/reading/my?status=bad")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/reading/my/books/bad", ReadingPersistenceTests.Request())).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/reading/my/books/" + ObjectId.GenerateNewId(), ReadingPersistenceTests.Request())).StatusCode);
            Assert.Single((await client.GetFromJsonAsync<ReadingListResponse>("/api/reading/my"))!.Items);
            client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", f.User.Id);
            Assert.Empty((await client.GetFromJsonAsync<ReadingListResponse>("/api/reading/my"))!.Items);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(url, ReadingPersistenceTests.Request(80, entry.Revision))).StatusCode);
            client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", user.Id);
            await f.Db.Users.UpdateOneAsync(u => u.Id == user.Id, Builders<User>.Update.Set(u => u.CredentialVersion, 1));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/reading/my")).StatusCode);
        }
    });
}
