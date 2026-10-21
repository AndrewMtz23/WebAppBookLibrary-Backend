using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Tests;

public sealed class CopyHttpTests
{
    [LocalMongoFact]
    public Task Staff_routes_reject_readers_invalid_pages_and_stale_updates()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await CopyPersistenceTests.Seed(db);
        var reader=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test",o=>{
            o.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};
            o.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};
        });
        builder.Services.AddAuthorization(o=>o.AddPolicy("ManageCopies",p=>p.RequireRole("admin","librarian")));
        builder.Services.AddControllers().AddApplicationPart(typeof(NotificationsController).Assembly);
        builder.Services.AddSingleton(f.Db);builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddScoped<CopyStore>();builder.Services.AddScoped<CopyQueries>();builder.Services.AddScoped<CopyTransitions>();
        await using var app=builder.Build();
        app.Use(async(context,next)=>{
            if(context.Request.Headers.TryGetValue("Test-User",out var id)){
                var user=await f.Db.Users.Find(u=>u.Id==id.ToString()).FirstOrDefaultAsync();
                if(user!=null)context.User=new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier,user.Id),new(ClaimTypes.Role,user.Role)],"test"));
            }
            await next(context);
        });
        app.UseAuthentication();app.UseAuthorization();app.MapControllers();await app.StartAsync();
        using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};
        var path=$"/api/books/{f.Book.Id}/copies";
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Add("Test-User",reader.Id);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-User");client.DefaultRequestHeaders.Add("Test-User",f.Actor.UserId);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync(path+"?pageSize=101")).StatusCode);
        var created=await client.PostAsJsonAsync(path,new CreateCopyRequest("HTTP-001","good",null,"Internal","http"));
        Assert.Equal(HttpStatusCode.OK,created.StatusCode);
        var copy=(await created.Content.ReadFromJsonAsync<CopyResponse>())!;
        Assert.Equal(copy.Id,(await client.GetFromJsonAsync<CopyResponse>("/api/copies/lookup?code=http-001"))!.Id);
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync($"/api/copies/{copy.Id}",new UpdateCopyRequest(1,"Shelf",null))).StatusCode);
        var conflict=await client.PutAsJsonAsync($"/api/copies/{copy.Id}",new UpdateCopyRequest(1,"Other",null));
        Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);Assert.Equal("application/problem+json",conflict.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PutAsJsonAsync($"/api/copies/{copy.Id}/status",new ChangeCopyStatusRequest("retired","Withdrawn",2,"retire"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.PutAsJsonAsync($"/api/copies/{copy.Id}/inspection",new InspectCopyRequest(2,new("damaged","Repair",null)))).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-User");client.DefaultRequestHeaders.Add("Test-User",reader.Id);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync($"/api/copies/{copy.Id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await client.GetAsync("/api/copies/lookup?code=HTTP-001")).StatusCode);
    });
}
