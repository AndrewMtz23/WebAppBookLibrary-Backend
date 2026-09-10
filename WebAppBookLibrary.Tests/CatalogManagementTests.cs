using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class CatalogManagementTests
{
    [Theory]
    [InlineData(RoleNames.User, 403)]
    [InlineData(RoleNames.Librarian, 200)]
    [InlineData(RoleNames.Admin, 200)]
    public async Task Management_is_protected_and_only_management_exposes_resource(string role, int expected)
    {
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Digital", DigitalResourceUrl = "https://library.example/private.pdf" };
        var store = new Mock<IBookStore>();
        store.Setup(x => x.FindByIdAsync(book.Id, It.IsAny<CancellationToken>())).ReturnsAsync(book);
        store.Setup(x => x.FindCatalogEntryAsync(book.Id, It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BookCatalogEntry(book, 0, false));
        await using var app = await StartApp(store.Object, role);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await client.GetAsync($"/api/books/{book.Id}/management");
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 200) Assert.Contains(book.DigitalResourceUrl, await response.Content.ReadAsStringAsync());
        var ordinary = await client.GetStringAsync($"/api/books/{book.Id}");
        Assert.DoesNotContain("digitalResourceUrl", ordinary);
        Assert.DoesNotContain(book.DigitalResourceUrl, ordinary);
    }

    [Theory]
    [InlineData(RoleNames.User)]
    [InlineData(RoleNames.Librarian)]
    public async Task Permanent_delete_denies_non_admin_through_Mvc(string role)
    {
        await using var app = await StartApp(new Mock<IBookStore>().Object, role);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/books/{ObjectId.GenerateNewId()}/permanent")).StatusCode);
    }

    internal static async Task<WebApplication> StartApp(IBookStore store, string role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", o => o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; });
        Program.ConfigureAuthorizationPolicies(builder.Services);
        builder.Services.AddControllers().AddApplicationPart(typeof(BooksController).Assembly).AddControllersAsServices();
        builder.Services.AddTransient(_ => new BooksController(new BookService(store), null!));
        var app = builder.Build();
        app.Use((context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "staff"), new Claim(ClaimTypes.Role, role)], "test")); return next(context); });
        app.UseAuthorization(); app.MapControllers(); await app.StartAsync(); return app;
    }
}
