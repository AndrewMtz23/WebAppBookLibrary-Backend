using System.Net.Http.Json;
using System.Security.Claims;
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

public sealed class CatalogHttpQueryTests
{
    [Fact]
    public async Task Flat_query_parameters_reach_the_catalog_through_real_Mvc_binding()
    {
        var store = new Mock<IBookStore>();
        var books = new[]
        {
            new Book { Id = "000000000000000000000001", Title = "Frankenstein", Authors = ["Mary Shelley"], MediaType = "digital", IsActive = true },
            new Book { Id = "000000000000000000000002", Title = "La máquina del tiempo", Authors = ["H. G. Wells"], MediaType = "physical", IsActive = true }
        };
        NormalizedBookQuery? received = null;
        store.Setup(s => s.SearchAsync(It.IsAny<NormalizedBookQuery>(), false, "reader", It.IsAny<CancellationToken>()))
            .ReturnsAsync((NormalizedBookQuery query, bool _, string? __, CancellationToken ___) =>
            {
                received = query;
                var matches = books.Where(b => query.Query is null || b.Title.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
                    .Select(b => new BookCatalogEntry(b, 0, false)).ToArray();
                return new PagedResult<BookCatalogEntry>(matches, query.Page, query.PageSize, matches.Length);
            });
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(BooksController).Assembly).AddControllersAsServices();
        builder.Services.AddTransient(_ => new BooksController(new BookService(store.Object), null!));
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader"), new Claim(ClaimTypes.Role, "user")], "test"));
            return next(context);
        });
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        var response = await client.GetFromJsonAsync<PagedResult<BookSummaryResponse>>("/api/books?query=Frankenstein&mediaType=digital&isActive=false&lowStock=true&missingResource=true&pageSize=5&sort=relevance");

        Assert.NotNull(received);
        Assert.Equal("Frankenstein", received.Query);
        Assert.Equal("digital", received.MediaType);
        Assert.Equal(5, received.PageSize);
        Assert.Equal("relevance", received.Sort);
        Assert.False(received.IsActive);
        Assert.True(received.LowStock);
        Assert.True(received.MissingResource);
        Assert.Equal("Frankenstein", Assert.Single(response!.Items).Title);
    }
}
