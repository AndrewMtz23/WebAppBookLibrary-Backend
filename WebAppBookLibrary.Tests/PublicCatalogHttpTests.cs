using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class PublicCatalogHttpTests
{
    [Fact]
    public async Task Anonymous_reads_are_public_but_private_operations_stay_protected()
    {
        const string id = "000000000000000000000001";
        var book = new Book { Id = id, Title = "Public book", Authors = ["Author"], MediaType = "digital", IsActive = true, DigitalResourceUrl = "https://example.invalid/private-resource" };
        var store = new Mock<IBookStore>(MockBehavior.Strict);
        store.Setup(s => s.SearchAsync(It.IsAny<NormalizedBookQuery>(), false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<BookCatalogEntry>([new(book, 0, false)], 1, 20, 1));
        store.Setup(s => s.FindCatalogEntryAsync(id, false, null, It.IsAny<CancellationToken>())).ReturnsAsync(new BookCatalogEntry(book, 0, false));
        store.Setup(s => s.FindCatalogEntryAsync("000000000000000000000002", false, null, It.IsAny<CancellationToken>())).ReturnsAsync((BookCatalogEntry?)null);
        store.Setup(s => s.GetGenreFacetsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<BookFacetResponse>());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        Program.ConfigureAuthorizationPolicies(builder.Services);
        builder.Services.AddControllers().AddApplicationPart(typeof(BooksController).Assembly).AddControllersAsServices();
        builder.Services.AddTransient(_ => new BooksController(new BookService(store.Object), null!));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var path in new[] { "/api/books?isActive=false", "/api/books/facets", $"/api/books/{id}" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-resource", body);
            Assert.DoesNotContain("digitalResourceUrl", body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/books/000000000000000000000002")).StatusCode);
        foreach (var path in new[] { $"/api/books/{id}/management", $"/api/books/{id}/digital-access", "/api/favorites", "/api/profile/me" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(method, method == HttpMethod.Post ? "/api/books" : $"/api/books/{id}"))).StatusCode);
    }
}
