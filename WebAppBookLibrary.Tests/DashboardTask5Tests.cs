using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Dashboard;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class DashboardTask5Tests
{
    [Theory]
    [InlineData(RoleNames.User, "/api/dashboard/librarian", HttpStatusCode.Forbidden)]
    [InlineData(RoleNames.Librarian, "/api/dashboard/librarian", HttpStatusCode.OK)]
    [InlineData(RoleNames.Librarian, "/api/dashboard/admin", HttpStatusCode.Forbidden)]
    [InlineData(RoleNames.Admin, "/api/dashboard/librarian", HttpStatusCode.OK)]
    [InlineData(RoleNames.Admin, "/api/dashboard/admin", HttpStatusCode.OK)]
    public async Task Dashboard_endpoints_apply_the_production_role_matrix(string role, string path, HttpStatusCode expected)
    {
        var store = DashboardStoreMock();
        await using var app = await StaffHttpQueryTests.StartApp(
            services => services.AddTransient(_ => new DashboardController(new DashboardService(store.Object))), role);
        using var client = StaffHttpQueryTests.Client(app);

        var response = await client.GetAsync(path + "?from=2026-09-01&to=2026-09-02&timezone=UTC");

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Dashboard_http_binding_returns_timezone_bounds_and_safe_period_errors()
    {
        DashboardPeriod? received = null;
        var store = DashboardStoreMock();
        store.Setup(item => item.LibrarianAsync(It.IsAny<DashboardPeriod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DashboardPeriod period, CancellationToken _) => { received = period; return EmptyLibrarian(period); });
        await using var app = await StaffHttpQueryTests.StartApp(
            services => services.AddTransient(_ => new DashboardController(new DashboardService(store.Object))), RoleNames.Librarian);
        using var client = StaffHttpQueryTests.Client(app);

        var ok = await client.GetAsync("/api/dashboard/librarian?from=2026-03-07&to=2026-03-09&timezone=America%2FNew_York");
        var invalid = await client.GetAsync("/api/dashboard/librarian?from=9999-12-31&to=9999-12-31&timezone=UTC");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("America/New_York", received!.Timezone);
        Assert.Equal(new DateTime(2026, 3, 7, 5, 0, 0, DateTimeKind.Utc), received.FromUtc);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("date_overflow", await invalid.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Period_uses_inclusive_local_dates_and_same_length_previous_period()
    {
        Assert.True(DashboardPeriod.TryCreate(
            new DateOnly(2026, 3, 7),
            new DateOnly(2026, 3, 9),
            "America/New_York",
            out var period,
            out var error));

        Assert.Equal(string.Empty, error);
        Assert.Equal(new DateTime(2026, 3, 7, 5, 0, 0, DateTimeKind.Utc), period!.FromUtc);
        Assert.Equal(new DateTime(2026, 3, 10, 4, 0, 0, DateTimeKind.Utc), period.ToUtc);
        Assert.Equal(new DateTime(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc), period.PreviousFromUtc);
        Assert.Equal(new DateTime(2026, 3, 7, 5, 0, 0, DateTimeKind.Utc), period.PreviousToUtc);
        Assert.Equal(3, period.LocalCalendarDays);
    }

    [Fact]
    public void Period_rejects_overflowing_end_date_without_throwing()
    {
        var ok = DashboardPeriod.TryCreate(DateOnly.MaxValue, DateOnly.MaxValue, "UTC", out _, out var error);

        Assert.False(ok);
        Assert.Equal("date_overflow", error);
    }

    [Fact]
    public void Period_rejects_platform_specific_non_iana_timezone_ids()
    {
        var ok = DashboardPeriod.TryCreate(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), "Central Standard Time", out _, out var error);

        Assert.False(ok);
        Assert.Equal("invalid_timezone", error);
    }

    [Fact]
    public void Comparison_has_no_percentage_when_previous_period_is_zero()
    {
        Assert.Equal(new DashboardComparison(4, 0, null), DashboardComparison.From(4, 0));
        Assert.Equal(50m, DashboardComparison.From(3, 2).PercentageChange);
    }

    [LocalMongoFact]
    public Task Staff_dashboards_reconcile_current_stock_period_activity_legacy_dates_and_rankings() =>
        StaffReviewRegressionTests.WithDatabase(async database =>
        {
            var books = database.GetCollection<Book>("Books");
            var loans = database.GetCollection<Loan>("Loans");
            var users = database.GetCollection<User>("Users");
            var logs = database.GetCollection<LogEntry>("LogEntries");
            var ids = Enumerable.Range(0, 7).Select(_ => ObjectId.GenerateNewId().ToString()).ToArray();
            await books.InsertManyAsync([
                Book(ids[0], "Low", "physical", true, 4, 1, ["History"]),
                Book(ids[1], "Out", "physical", true, 2, 0, ["History", "Mystery"]),
                Book(ids[2], "Shelf", "physical", true, 3, 3, ["Essay"]),
                Book(ids[3], "Digital", "digital", true, null, null, ["Essay"]),
                Book(ids[4], "Quiet", "digital", true, null, null, ["Poetry"]),
                Book(ids[5], "Inactive", "physical", false, 5, 5, ["Hidden"])
            ]);
            await database.GetCollection<BsonDocument>("Books").InsertOneAsync(new BsonDocument
            {
                { "_id", ObjectId.Parse(ids[6]) }, { "Title", "Legacy low" }, { "Genre", "Legacy" }, { "IsAvailable", true }
            });
            await users.InsertManyAsync([
                User("active-admin", true, "admin"),
                User("active-reader", true, "user"),
                User("inactive-reader", false, "user")
            ]);

            var period = new DashboardPeriod(
                new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 12, 6, 0, 0, DateTimeKind.Utc),
                "America/Mexico_City",
                new DateTime(2026, 9, 12, 18, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 8, 6, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 10, 6, 0, 0, DateTimeKind.Utc),
                2);

            await loans.InsertManyAsync([
                // Keep this loan future-due both at the dashboard snapshot and at the live list's clock.
                Loan(ids[0], "physical", "active", new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), DateTime.UtcNow.AddYears(1)),
                Loan(ids[1], "physical", "active", new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
                Loan(ids[0], "physical", "returned", new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc), null, new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc)),
                Loan(ids[3], "digital", "active", new DateTime(2026, 9, 10, 7, 0, 0, DateTimeKind.Utc), null),
                Loan(ids[3], "digital", "cancelled", new DateTime(2026, 9, 11, 7, 0, 0, DateTimeKind.Utc), null),
                Loan(ids[0], "physical", "returned", new DateTime(2026, 9, 8, 8, 0, 0, DateTimeKind.Utc), null, new DateTime(2026, 9, 8, 9, 0, 0, DateTimeKind.Utc)),
                Loan(ids[3], "digital", "", new DateTime(2026, 8, 5, 8, 0, 0, DateTimeKind.Utc), null),
                new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = ids[2], UserId = ObjectId.GenerateNewId().ToString(), ReservedAt = default, LoanDate = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc), MediaType = "", Status = "", IsReturned = false },
                new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = ids[6], UserId = ObjectId.GenerateNewId().ToString(), ReservedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), LoanDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), MediaType = "  ", Status = "active", DueAt = null, IsReturned = false },
                new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = ids[1], UserId = ObjectId.GenerateNewId().ToString(), ReservedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), LoanDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), MediaType = "physical", Status = "cancelled", ReturnedAt = new DateTime(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc) }
            ]);
            await logs.InsertOneAsync(new LogEntry { Id = ObjectId.GenerateNewId().ToString(), Timestamp = new DateTime(2026, 9, 11, 4, 0, 0, DateTimeKind.Utc), EventType = "book.updated", ActorUsername = "active-admin", TargetType = "book", TargetId = ids[0] });
            await logs.InsertManyAsync(Enumerable.Range(0, 12).Select(index => new LogEntry
            {
                Id = ObjectId.GenerateNewId().ToString(), Timestamp = new DateTime(2026, 9, 11, 5, 0, 0, DateTimeKind.Utc).AddMinutes(index),
                EventType = index % 2 == 0 ? "authentication.succeeded" : "user.favorite_added", ActorUsername = "active-reader", TargetType = "user", TargetId = "reader"
            }));

            var store = new MongoDashboardStore(new MongoDBService(database));
            var librarian = await store.LibrarianAsync(period, default);
            var admin = await store.AdminAsync(period, default);
            var activity = await store.AdminActivityAsync(period, default);

            Assert.Equal(3, librarian.TotalReservations);
            Assert.Equal(4, librarian.ActiveReservations);
            Assert.Equal(1, librarian.OverdueReservations);
            Assert.Equal(5, librarian.AvailablePhysicalCopies);
            Assert.Equal(new DashboardComparison(1, 1, 0), librarian.ReturnedPhysical);
            Assert.Equal(new DashboardComparison(2, 0, null), librarian.DigitalReservations);
            Assert.Equal(2, librarian.LowInventoryTitles);
            Assert.Equal(1, librarian.OutOfStockTitles);
            Assert.Equal(ids[3], librarian.TopReservedTitles[0].Id);
            Assert.Contains(librarian.TitlesWithoutReservations, item => item.Id == ids[4]);

            Assert.Equal(2, admin.ActiveUsers);
            Assert.Equal(7, admin.TotalBooks);
            Assert.Equal(6, admin.ActiveTitles);
            Assert.Equal(6, admin.OutstandingReservations);
            Assert.Equal(1, admin.OverdueReservations);
            Assert.Equal(2, admin.DailyReservations.Count);
            Assert.Equal(3, admin.DailyReservations.Sum(day => day.Physical + day.Digital));
            Assert.Equal(new DashboardComparison(3, 1, 200), admin.PeriodReservations);
            Assert.Equal(1, admin.InactiveAccounts);
            Assert.Equal(3, admin.InventoryAttentionTitles);
            Assert.Equal(2, admin.LowInventoryTitles);
            Assert.Equal(1, admin.OutOfStockTitles);
            Assert.Single(activity);
            Assert.Equal("book.updated", activity[0].EventType);

            var loanList = new MongoLoanStore(new MongoDBService(database));
            var overdueList = await loanList.SearchDetailsAsync(new LoanQuery { Status = "overdue", MediaType = "physical" }.Normalize(), default);
            var returnedList = await loanList.SearchDetailsAsync(new LoanQuery { Status = "returned", MediaType = "physical", DateField = "returnedAt", From = period.FromUtc, To = period.ToUtc }.Normalize(), default);
            var digitalList = await loanList.SearchDetailsAsync(new LoanQuery { MediaType = "digital", From = period.FromUtc, To = period.ToUtc }.Normalize(), default);
            var bookList = new MongoBookStore(new MongoDBService(database));
            var lowList = await bookList.SearchAsync(new BookQuery { LowStock = true }.Normalize(), true, null, default);
            var outList = await bookList.SearchAsync(new BookQuery { IsActive = true, MediaType = "physical", Available = false }.Normalize(), true, null, default);
            var exactBook = await bookList.SearchAsync(new BookQuery { BookId = ids[4], IsActive = true }.Normalize(), true, null, default);
            var legacyGenre = await bookList.SearchAsync(new BookQuery { Genre = "Legacy", IsActive = true, MediaType = "physical", LowStock = true }.Normalize(), true, null, default);
            Assert.Equal(librarian.OverdueReservations, overdueList.TotalItems);
            Assert.Equal(librarian.ReturnedPhysical.Current, returnedList.TotalItems);
            Assert.Equal(librarian.DigitalReservations.Current, digitalList.TotalItems);
            Assert.Equal(librarian.LowInventoryTitles, lowList.TotalItems);
            Assert.Equal(librarian.OutOfStockTitles, outList.TotalItems);
            Assert.Equal(ids[4], Assert.Single(exactBook.Items).Book.Id);
            Assert.Equal(ids[6], Assert.Single(legacyGenre.Items).Book.Id);
        });

    private static Book Book(string id, string title, string media, bool active, int? total, int? available, List<string> genres) =>
        new() { Id = id, Title = title, MediaType = media, IsActive = active, TotalCopies = total, AvailableCopies = available, Genres = genres };

    private static User User(string username, bool active, string role) =>
        new() { Id = ObjectId.GenerateNewId().ToString(), Username = username, IsActive = active, Role = role };

    private static Loan Loan(string bookId, string media, string status, DateTime reserved, DateTime? due, DateTime? returned = null) =>
        new() { Id = ObjectId.GenerateNewId().ToString(), BookId = bookId, UserId = ObjectId.GenerateNewId().ToString(), MediaType = media, Status = status, ReservedAt = reserved, LoanDate = reserved, DueAt = due, ReturnedAt = returned, ReturnDate = returned, IsReturned = status == "returned" };

    private static Mock<IDashboardStore> DashboardStoreMock()
    {
        var store = new Mock<IDashboardStore>();
        store.Setup(item => item.LibrarianAsync(It.IsAny<DashboardPeriod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DashboardPeriod period, CancellationToken _) => EmptyLibrarian(period));
        store.Setup(item => item.AdminAsync(It.IsAny<DashboardPeriod>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DashboardPeriod period, CancellationToken _) => new AdminDashboardResponse(
                period.GeneratedAt, period.FromUtc, period.ToUtc, 0, 0, 0, [], [], period.Timezone,
                period.PreviousFromUtc, period.PreviousToUtc, 0, 0, 0, DashboardComparison.From(0, 0), [], [], [], [], 0, 0, [], 0, 0));
        store.Setup(item => item.AdminActivityAsync(It.IsAny<DashboardPeriod>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return store;
    }

    private static LibrarianDashboardResponse EmptyLibrarian(DashboardPeriod period) => new(
        period.GeneratedAt, period.FromUtc, period.ToUtc, 0, 0, 0, 0, 0, [], period.Timezone,
        period.PreviousFromUtc, period.PreviousToUtc, DashboardComparison.From(0, 0), DashboardComparison.From(0, 0), 0, 0, [], []);
}
