using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
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
    private const string StaffId = "507f1f77bcf86cd799439011";
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

    [Fact]
    public async Task Admin_role_attempt_uses_canonical_ids_returns_conflict_and_audits_failure()
    {
        var target = ObjectId.GenerateNewId().ToString();
        var store = new Mock<IAdminUserStore>();
        store.Setup(x => x.SetRoleSafelyAsync(StaffId, target, RoleNames.User, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminStoreMutationResult(AdminStoreMutationOutcome.LastAdmin, "staff", "target-admin", RoleNames.Admin, true, RoleNames.User, true));
        var audit = new RecordingAdminUserAudit();
        await using var app = await StartApp(services =>
        {
            services.AddSingleton(audit);
            services.AddSingleton<IAdminUserAudit>(audit);
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(store.Object), audit));
        }, RoleNames.Admin);
        using var client = Client(app);

        var response = await client.PutAsJsonAsync($"/api/admin/users/{target.ToUpperInvariant()}/role", new { role = "USER" });

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(("role_change_attempt", StaffId, target), (entry.Action, entry.ActorId, entry.TargetId));
        Assert.Equal("failed", entry.Metadata["result"]);
        Assert.Equal(AdminUserErrorCodes.LastAdmin, entry.Metadata["reasonCode"]);
        Assert.Equal(RoleNames.Admin, entry.Metadata["previousRole"]);
        Assert.Equal(RoleNames.User, entry.Metadata["newRole"]);
    }

    [Fact]
    public async Task Invalid_admin_mutation_identifier_is_rejected_and_audited_without_raw_value()
    {
        var store = new Mock<IAdminUserStore>();
        var audit = new RecordingAdminUserAudit();
        await using var app = await StartApp(services => services.AddTransient(_ => new AdminUsersController(new AdminUserService(store.Object), audit)), RoleNames.Admin);
        using var client = Client(app);

        var response = await client.PutAsJsonAsync("/api/admin/users/not-an-object-id/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("invalid", entry.TargetId);
        Assert.Equal("invalid_identifier", entry.Metadata["reasonCode"]);
        Assert.DoesNotContain("not-an-object-id", entry.TargetId);
    }

    [Fact]
    public async Task Invalid_role_payload_is_rejected_and_audited_before_store_mutation()
    {
        var target = ObjectId.GenerateNewId().ToString();
        var store = new Mock<IAdminUserStore>();
        var audit = new RecordingAdminUserAudit();
        await using var app = await StartApp(services =>
        {
            services.AddSingleton(audit);
            services.AddSingleton<IAdminUserAudit>(audit);
            services.AddScoped<AdminMutationValidationAuditFilter>();
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(store.Object), audit));
        }, RoleNames.Admin);
        using var client = Client(app);

        var response = await client.PutAsJsonAsync($"/api/admin/users/{target}/role", new { role = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(("role_change_attempt", StaffId, target), (entry.Action, entry.ActorId, entry.TargetId));
        Assert.Equal("failed", entry.Metadata["result"]);
        Assert.Equal("invalid_request", entry.Metadata["reasonCode"]);
        store.Verify(x => x.SetRoleSafelyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Malformed_status_payload_is_rejected_and_audited_before_store_mutation()
    {
        var target = ObjectId.GenerateNewId().ToString();
        var store = new Mock<IAdminUserStore>();
        var audit = new RecordingAdminUserAudit();
        await using var app = await StartApp(services =>
        {
            services.AddSingleton(audit);
            services.AddSingleton<IAdminUserAudit>(audit);
            services.AddScoped<AdminMutationValidationAuditFilter>();
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(store.Object), audit));
        }, RoleNames.Admin);
        using var client = Client(app);

        var response = await client.PutAsJsonAsync($"/api/admin/users/{target}/status", new { isActive = "not-a-boolean" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(("status_change_attempt", StaffId, target), (entry.Action, entry.ActorId, entry.TargetId));
        Assert.Equal("failed", entry.Metadata["result"]);
        Assert.Equal("invalid_request", entry.Metadata["reasonCode"]);
        store.Verify(x => x.SetStatusSafelyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Successful_store_followed_by_audit_failure_returns_uncertain_server_error()
    {
        var target = ObjectId.GenerateNewId().ToString();
        var store = new Mock<IAdminUserStore>();
        store.Setup(x => x.SetRoleSafelyAsync(StaffId, target, RoleNames.Librarian, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AdminStoreMutationResult(AdminStoreMutationOutcome.Success, "staff", "target", RoleNames.User, true, RoleNames.Librarian, true));
        var audit = new ThrowingAdminUserAudit();
        await using var app = await StartApp(services =>
        {
            services.AddSingleton<IAdminUserAudit>(audit);
            services.AddScoped<AdminMutationValidationAuditFilter>();
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(store.Object), audit));
        }, RoleNames.Admin);
        using var client = Client(app);

        var response = await client.PutAsJsonAsync($"/api/admin/users/{target}/role", new { role = RoleNames.Librarian });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        store.Verify(x => x.SetRoleSafelyAsync(StaffId, target, RoleNames.Librarian, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [LocalMongoFact]
    public Task Admin_mutation_http_matrix_uses_real_store_protections_and_safe_audit() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var users = db.GetCollection<User>("Users");
        var targetId = ObjectId.GenerateNewId().ToString();
        await users.InsertManyAsync([
            TestUser(StaffId, "staff", RoleNames.Admin, true),
            TestUser(targetId, "target", RoleNames.User, true)
        ]);
        var store = new MongoAdminUserStore(users);
        await using var app = await StartApp(services =>
        {
            services.AddSingleton(new MongoDBService(db));
            services.AddHttpContextAccessor();
            services.AddSingleton<IAdminUserStore>(store);
            services.AddScoped<Logservice>();
            services.AddScoped<IAdminUserAudit>(provider => provider.GetRequiredService<Logservice>());
            services.AddScoped<AdminUserService>();
            services.AddTransient(provider => new AdminUsersController(provider.GetRequiredService<AdminUserService>(), provider.GetRequiredService<IAdminUserAudit>()));
        }, RoleNames.Admin);
        using var client = Client(app);

        var roleResponse = await client.PutAsJsonAsync($"/api/admin/users/{targetId}/role", new { role = RoleNames.Librarian });
        Assert.Equal(HttpStatusCode.NoContent, roleResponse.StatusCode);
        Assert.Empty(await roleResponse.Content.ReadAsStringAsync());
        using (var detailResponse = await client.GetAsync($"/api/admin/users/{targetId.ToUpperInvariant()}"))
        {
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
            var root = detail.RootElement;
            Assert.Equal(targetId, root.GetProperty("id").GetString());
            Assert.Equal("target", root.GetProperty("username").GetString());
            Assert.Equal(RoleNames.Librarian, root.GetProperty("role").GetString());
            Assert.False(root.TryGetProperty("passwordHash", out _));
            Assert.False(root.TryGetProperty("password", out _));
        }
        var statusResponse = await client.PutAsJsonAsync($"/api/admin/users/{targetId}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.NoContent, statusResponse.StatusCode);
        Assert.Equal(RoleNames.Librarian, (await users.Find(user => user.Id == targetId).FirstAsync()).Role);
        Assert.False((await users.Find(user => user.Id == targetId).FirstAsync()).IsActive);
        var logs = await db.GetCollection<LogEntry>("LogEntries").Find(_ => true).ToListAsync();
        Assert.Contains(logs, entry => entry.EventType == "user.role_change_attempt" && entry.ActorId == StaffId && entry.ActorUsername == "staff" && entry.TargetId == targetId && !string.IsNullOrWhiteSpace(entry.CorrelationId) && entry.Metadata["result"] == "success" && entry.Metadata["previousRole"] == RoleNames.User && entry.Metadata["newRole"] == RoleNames.Librarian);
        Assert.Contains(logs, entry => entry.EventType == "user.status_change_attempt" && entry.ActorId == StaffId && entry.ActorUsername == "staff" && entry.TargetId == targetId && !string.IsNullOrWhiteSpace(entry.CorrelationId) && entry.Metadata["result"] == "success" && entry.Metadata["previousStatus"] == "active" && entry.Metadata["newStatus"] == "inactive");

        var selfRole = await client.PutAsJsonAsync($"/api/admin/users/{StaffId}/role", new { role = RoleNames.User });
        var selfStatus = await client.PutAsJsonAsync($"/api/admin/users/{StaffId}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, selfRole.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, selfStatus.StatusCode);
        var selfLogs = await db.GetCollection<LogEntry>("LogEntries").Find(entry => entry.EventType == "user.role_change_attempt" || entry.EventType == "user.status_change_attempt").ToListAsync();
        Assert.Contains(selfLogs, entry => entry.TargetId == StaffId && entry.Metadata.TryGetValue("reasonCode", out var reason) && reason == AdminUserErrorCodes.SelfMutation && !string.IsNullOrWhiteSpace(entry.CorrelationId));
    });

    [Theory]
    [InlineData(RoleNames.User)]
    [InlineData(RoleNames.Librarian)]
    public async Task Admin_mutation_http_policy_denies_reader_and_librarian(string role)
    {
        var store = new Mock<IAdminUserStore>();
        var audit = new RecordingAdminUserAudit();
        var target = ObjectId.GenerateNewId().ToString();
        await using var app = await StartApp(services =>
        {
            services.AddSingleton<IAdminUserAudit>(audit);
            services.AddScoped<AdminMutationValidationAuditFilter>();
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(store.Object), audit));
        }, role);
        using var client = Client(app);

        var roleResponse = await client.PutAsJsonAsync($"/api/admin/users/{target}/role", new { role = RoleNames.Librarian });
        var statusResponse = await client.PutAsJsonAsync($"/api/admin/users/{target}/status", new { isActive = false });
        Assert.Equal(HttpStatusCode.Forbidden, roleResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, statusResponse.StatusCode);
        store.Verify(x => x.SetRoleSafelyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(x => x.SetStatusSafelyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    internal static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };
    internal static async Task<WebApplication> StartApp(Action<IServiceCollection> register, string role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test").AddCookie("test", options =>
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; });
        Program.ConfigureAuthorizationPolicies(builder.Services);
        builder.Services.AddControllers().AddApplicationPart(typeof(BooksController).Assembly).AddControllersAsServices();
        builder.Services.AddScoped<AdminMutationValidationAuditFilter>();
        register(builder.Services);
        var app = builder.Build();
        app.Use((context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "staff"), new Claim(ClaimTypes.NameIdentifier, StaffId), new Claim(ClaimTypes.Role, role)], "test")); return next(context); });
        app.UseAuthorization(); app.MapControllers(); await app.StartAsync(); return app;
    }

    private sealed class RecordingAdminUserAudit : IAdminUserAudit
    {
        public List<(string Action, string ActorId, string TargetId, IReadOnlyDictionary<string, string> Metadata)> Entries { get; } = [];
        public Task UserChangedAsync(string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Entries.Add((action, actorId, targetId, metadata ?? new Dictionary<string, string>()));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingAdminUserAudit : IAdminUserAudit
    {
        public Task UserChangedAsync(string action, string actorId, string targetId, IReadOnlyDictionary<string, string>? metadata = null) => throw new InvalidOperationException("audit unavailable");
    }

    private static User TestUser(string id, string username, string role, bool active) => new()
    {
        Id = id,
        Username = username,
        NormalizedUsername = username.ToUpperInvariant(),
        DisplayName = username,
        Email = $"{username}@example.test",
        NormalizedEmail = $"{username}@example.test".ToUpperInvariant(),
        Role = role,
        IsActive = active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}
