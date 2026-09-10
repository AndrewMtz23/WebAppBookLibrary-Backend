using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class StaffHttpQueryTests
{
    [LocalMongoFact]
    public Task Catalog_Mvc_with_real_store_enforces_reader_visibility_and_staff_filters() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var books = db.GetCollection<Book>("Books");
        var active = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Visible", IsActive = true, MediaType = "physical", AvailableCopies = 5 };
        var inactive = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Hidden", IsActive = false, MediaType = "digital" };
        await books.InsertManyAsync([active, inactive]);
        var store = new MongoBookStore(books, db.GetCollection<Loan>("Loans"), db.GetCollection<Favorite>("Favorites"), db.GetCollection<User>("Users"));
        foreach (var role in new[] { RoleNames.User, RoleNames.Librarian, RoleNames.Admin })
        {
            await using var app = await StartApp(services => services.AddTransient(_ => new BooksController(new BookService(store), null!)), role);
            using var client = Client(app);
            var query = role == RoleNames.User ? "?isActive=false&lowStock=true&missingResource=true" : "?isActive=false";
            var page = await client.GetFromJsonAsync<PagedResult<BookSummaryResponse>>("/api/books" + query);
            Assert.Equal(role == RoleNames.User ? active.Id : inactive.Id, Assert.Single(page!.Items).Id);
            Assert.Equal(1, page.TotalItems);
        }
    });

    [Theory]
    [InlineData(RoleNames.User, "/api/loans")]
    [InlineData(RoleNames.User, "/api/admin/users")]
    [InlineData(RoleNames.Librarian, "/api/admin/users")]
    public async Task Staff_endpoints_deny_roles_without_permission(string role, string path)
    {
        var loans = new Mock<ILoanStore>();
        loans.Setup(x => x.SearchDetailsAsync(It.IsAny<NormalizedLoanQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<LoanSearchEntry>([], 1, 20, 0));
        var users = new Mock<IAdminUserStore>();
        users.Setup(x => x.SearchAsync(It.IsAny<AdminUserQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PagedResult<WebAppBookLibrary.Models.User>([], 1, 20, 0));
        await using var app = await StartApp(services =>
        {
            services.AddTransient(_ => new LoansController(new LoanService(loans.Object)));
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(users.Object), null!));
        }, role);
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        loans.Verify(x => x.SearchDetailsAsync(It.IsAny<NormalizedLoanQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(x => x.SearchAsync(It.IsAny<AdminUserQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Reader_own_loans_overwrite_client_user_id_through_Mvc()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("staff")).ReturnsAsync(new WebAppBookLibrary.Models.User { Id = "000000000000000000000001", Username = "staff", IsActive = true });
        NormalizedLoanQuery? received = null;
        store.Setup(x => x.SearchAsync(It.IsAny<NormalizedLoanQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((NormalizedLoanQuery q, CancellationToken _) => { received = q; return new([], q.Page, q.PageSize, 0); });
        await using var app = await StartApp(services => services.AddTransient(_ => new LoansController(new LoanService(store.Object))), RoleNames.User);
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/loans/my?userId=000000000000000000000099&query=ana")).StatusCode);
        Assert.Equal("000000000000000000000001", received!.UserId);
        Assert.Equal("ana", received.Query);
    }

    [Fact]
    public async Task Admin_users_flat_query_binds_combined_operational_filters()
    {
        AdminUserQuery? received = null;
        var store = new Mock<IAdminUserStore>();
        store.Setup(x => x.SearchAsync(It.IsAny<AdminUserQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((AdminUserQuery q, CancellationToken _) => { received = q.Normalize(); return new([], q.Page, q.PageSize, 0); });
        await using var app = await StartApp(services => services.AddTransient(_ => new AdminUsersController(new AdminUserService(store.Object), null!)), RoleNames.Admin);
        using var client = Client(app);

        var response = await client.GetAsync("/api/admin/users?query=ana&createdFrom=2026-01-01T00:00:00Z&createdTo=2026-02-01T00:00:00Z&lastLoginFrom=2026-01-02T00:00:00Z&lastLoginTo=2026-02-02T00:00:00Z&sort=lastLoginAt&direction=desc&page=9&pageSize=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(received);
        Assert.Equal("lastLoginAt", received.Sort);
        Assert.Equal("desc", received.Direction);
        Assert.Equal(9, received.Page);
        Assert.Equal(7, received.PageSize);
        Assert.NotNull(received.CreatedFrom);
        Assert.NotNull(received.LastLoginTo);
    }

    [Theory]
    [InlineData(RoleNames.Librarian)]
    [InlineData(RoleNames.Admin)]
    public async Task Loans_flat_query_binds_combined_filters_and_empty_page_metadata(string role)
    {
        NormalizedLoanQuery? received = null;
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.SearchDetailsAsync(It.IsAny<NormalizedLoanQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((NormalizedLoanQuery q, CancellationToken _) => { received = q; return new PagedResult<LoanSearchEntry>([], q.Page, q.PageSize, 0); });
        await using var app = await StartApp(services => services.AddTransient(_ => new LoansController(new LoanService(store.Object))), role);
        using var client = Client(app);

        var response = await client.GetAsync("/api/loans?query=ana&status=outstanding&dueFrom=2026-01-01T00:00:00Z&dueTo=2026-02-01T00:00:00Z&dateField=returnedAt&from=2026-01-03T00:00:00Z&to=2026-02-03T00:00:00Z&sort=dueAt&direction=asc&page=99&pageSize=6");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(received);
        Assert.Equal("outstanding", received.Status);
        Assert.Equal("returnedAt", received.DateField);
        Assert.Equal("dueAt", received.Sort);
        Assert.Contains("\"page\":99", json);
        Assert.Contains("\"totalItems\":0", json);
    }

    [Fact]
    public async Task Invalid_date_range_is_rejected_by_real_Mvc_validation()
    {
        var store = new Mock<IAdminUserStore>();
        await using var app = await StartApp(services => services.AddTransient(_ => new AdminUsersController(new AdminUserService(store.Object), null!)), RoleNames.Admin);
        using var client = Client(app);
        var response = await client.GetAsync("/api/admin/users?createdFrom=2026-02-01T00:00:00Z&createdTo=2026-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        store.Verify(x => x.SearchAsync(It.IsAny<AdminUserQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
    private static async Task<WebApplication> StartApp(Action<IServiceCollection> register, string role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        Program.ConfigureAuthorizationPolicies(builder.Services);
        builder.Services.AddControllers().AddApplicationPart(typeof(BooksController).Assembly).AddControllersAsServices();
        register(builder.Services);
        var app = builder.Build();
        app.Use((context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "staff"), new Claim(ClaimTypes.Role, role)], "test")); return next(context); });
        app.UseAuthorization(); app.MapControllers(); await app.StartAsync(); return app;
    }
}
