using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Contracts.Reviews;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Services.Reviews;

namespace WebAppBookLibrary.Tests;
public sealed class ReviewHttpTests
{
    [LocalMongoFact]
    public Task Http_contract_rejects_unauthorized_invalid_and_conflicting_requests() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReviewPersistenceTests.Seed(db);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", o => {
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization(o => o.AddPolicy("ManageReviews", p => p.RequireRole("admin")));
        builder.Services.AddControllers().AddApplicationPart(typeof(NotificationsController).Assembly);
        builder.Services.AddSingleton(f.Db); builder.Services.AddSingleton<TimeProvider>(new SupportPersistenceTests.Clock());
        builder.Services.Configure<ReviewOptions>(o => o.PublicationsPerHour=1); builder.Services.AddScoped<MongoReviewStore>(); builder.Services.AddScoped<ReviewQueries>(); builder.Services.AddScoped<ReviewModerationStore>();
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
        var path=$"/api/books/{f.Book.Id}/reviews";
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path+"/my")).StatusCode);
        client.DefaultRequestHeaders.Add("Test-User",f.User.Id);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/admin/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(path+"?pageSize=101")).StatusCode);
        var created=await client.PutAsJsonAsync(path+"/my",new SaveReview(5,"Text",0));Assert.Equal(HttpStatusCode.OK,created.StatusCode);
        var review=(await created.Content.ReadFromJsonAsync<MyReview>())!;
        var conflict=await client.PutAsJsonAsync(path+"/my",new SaveReview(3,"Changed",0));Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);Assert.Equal("application/problem+json",conflict.Content.Headers.ContentType!.MediaType);
        var limited=await client.PutAsJsonAsync(path+"/my",new SaveReview(3,"Changed",1));Assert.Equal(HttpStatusCode.TooManyRequests,limited.StatusCode);Assert.NotNull(limited.Headers.RetryAfter);
        var reporter=await SupportPersistenceTests.UserAsync(f.Db,"user");await f.Db.Users.UpdateOneAsync(u=>u.Id==reporter.Id,MongoDB.Driver.Builders<WebAppBookLibrary.Models.User>.Update.Set(u=>u.EmailVerifiedAt,DateTime.UtcNow));
        client.DefaultRequestHeaders.Remove("Test-User");client.DefaultRequestHeaders.Add("Test-User",reporter.Id);
        var report=await client.PostAsJsonAsync($"/api/reviews/{review.Id}/reports",new ReportReview("Reason"));Assert.Equal(HttpStatusCode.OK,report.StatusCode);Assert.Equal("application/json",report.Content.Headers.ContentType!.MediaType);Assert.NotEmpty((await report.Content.ReadFromJsonAsync<string>())!);
        client.DefaultRequestHeaders.Remove("Test-User");client.DefaultRequestHeaders.Add("Test-User",admin.Id);
        Assert.Equal(HttpStatusCode.NotFound,(await client.GetAsync($"/api/reviews/{review.Id}/my")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync($"/api/admin/reviews/{review.Id}/visibility",new SetReviewVisibility("hidden","Reason",1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/admin/review-reports")).StatusCode);
    });
}
