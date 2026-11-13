using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Tests;

public sealed class BranchHttpTests
{
    [LocalMongoFact]
    public Task Public_metadata_is_minimal_and_admin_mutations_revalidate_accounts() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", o => {
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(BooksController).Assembly);
        builder.Services.AddSingleton(f.Mongo); builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<BranchStore>();
        await using var app = builder.Build();
        app.Use(async (c, next) => {
            if (c.Request.Headers.TryGetValue("Test-User", out var id))
                c.User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, "admin")], "test"));
            await next(c);
        });
        app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        var publicResponse = await client.GetAsync("/api/branches");
        Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
        Assert.DoesNotContain("referenceVersion", await publicResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, (await publicResponse.Content.ReadFromJsonAsync<PublicBranch[]>())!.Length);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/branches/my")).StatusCode);
        client.DefaultRequestHeaders.Add("Test-User", f.Staff.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/branches", new CreateBranchRequest("C", "C", "UTC"))).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-User"); client.DefaultRequestHeaders.Add("Test-User", f.Admin.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/branches", new CreateBranchRequest("C", "C", "UTC"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/branches", new CreateBranchRequest("c", "C", "UTC"))).StatusCode);
        await f.Mongo.Users.UpdateOneAsync(u => u.Id == f.Admin.Id, MongoDB.Driver.Builders<WebAppBookLibrary.Models.User>.Update.Set(u => u.IsActive, false));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/branches/my")).StatusCode);
    });
}
