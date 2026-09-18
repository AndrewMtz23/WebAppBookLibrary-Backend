using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class AdminUserDeletionTests
{
    private const string ActorId = "507f1f77bcf86cd799439011";

    [LocalMongoFact]
    public Task Concurrent_reference_creation_and_deletion_leave_no_orphans() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db);
        await database.Users.InsertOneAsync(new User { Id = ActorId, Username = "staff", Role = RoleNames.Admin });
        var store = new MongoAdminUserStore(database);
        for (var i = 0; i < 12; i++)
        {
            var id = ObjectId.GenerateNewId().ToString();
            var bookId = ObjectId.GenerateNewId().ToString();
            await database.Users.InsertOneAsync(new User { Id = id });
            await database.Books.InsertOneAsync(new Book { Id = bookId, MediaType = "digital" });
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Insert()
            {
                await gate.Task;
                try
                {
                    if (i % 2 == 0) await new MongoFavoriteStore(database).InsertAsync(new Favorite { Id = ObjectId.GenerateNewId().ToString(), UserId = id, BookId = bookId }, default);
                    else await new MongoLoanStore(database).InsertLoanAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = id, BookId = bookId, MediaType = "digital" }, default);
                }
                catch (UserReferenceUnavailableException) { }
            }
            async Task Delete()
            {
                await gate.Task;
                Assert.Equal(AdminStoreMutationOutcome.Success, (await store.SetStatusSafelyAsync(ActorId, id, false, DateTime.UtcNow, default)).Outcome);
                await store.DeletePermanentlyAsync(ActorId, id, default);
            }
            var insert = Insert(); var delete = Delete(); gate.SetResult();
            await Task.WhenAll(insert, delete);
            var exists = await database.Users.Find(u => u.Id == id).AnyAsync();
            var referenced = await database.Loans.Find(l => l.UserId == id).AnyAsync() || await database.Favorites.Find(f => f.UserId == id).AnyAsync();
            Assert.False(!exists && referenced);
        }
    });

    [LocalMongoFact]
    public Task Stale_requests_cannot_add_references_to_an_inactive_or_deleted_account() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db);
        var id = ObjectId.GenerateNewId().ToString();
        var bookId = ObjectId.GenerateNewId().ToString();
        await database.Users.InsertOneAsync(new User { Id = id, IsActive = false });
        await database.Books.InsertOneAsync(new Book { Id = bookId, MediaType = "digital", IsActive = true });
        foreach (var deleted in new[] { false, true })
        {
            if (deleted) await database.Users.DeleteOneAsync(u => u.Id == id);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new MongoFavoriteStore(database).InsertAsync(
                new Favorite { Id = ObjectId.GenerateNewId().ToString(), BookId = bookId, UserId = id }, default));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => new MongoLoanStore(database).InsertLoanAsync(
                new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = bookId, UserId = id, MediaType = "digital" }, default));
        }
        Assert.Equal(0, await database.Favorites.CountDocumentsAsync(FilterDefinition<Favorite>.Empty));
        Assert.Equal(0, await database.Loans.CountDocumentsAsync(FilterDefinition<Loan>.Empty));
    });

    [LocalMongoFact]
    public Task Logical_delete_is_reversible_and_physical_delete_removes_account_and_favorites() =>
        StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var users = db.GetCollection<User>("Users");
        var target = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "reader", Role = RoleNames.User };
        await users.InsertManyAsync([new User { Id = ActorId, Username = "staff", Role = RoleNames.Admin }, target]);
        var audit = new Mock<IAdminUserAudit>();
        await using var app = await StaffHttpQueryTests.StartApp(services =>
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(new MongoAdminUserStore(users)), audit.Object)), RoleNames.Admin);
        using var client = StaffHttpQueryTests.Client(app);
        var url = $"/api/admin/users/{target.Id}";

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(url + "/permanent")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url + "/status", new { isActive = false })).StatusCode);
        Assert.False((await users.Find(u => u.Id == target.Id).SingleAsync()).IsActive);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(url + "/status", new { isActive = true })).StatusCode);
        Assert.True((await users.Find(u => u.Id == target.Id).SingleAsync()).IsActive);
        await client.PutAsJsonAsync(url + "/status", new { isActive = false });
        await db.GetCollection<Favorite>("Favorites").InsertOneAsync(new Favorite { Id = ObjectId.GenerateNewId().ToString(), UserId = target.Id, BookId = ObjectId.GenerateNewId().ToString() });

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url + "/permanent")).StatusCode);
        Assert.False(await users.Find(u => u.Id == target.Id).AnyAsync());
        Assert.False(await db.GetCollection<Favorite>("Favorites").Find(f => f.UserId == target.Id).AnyAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(url + "/permanent")).StatusCode);
        audit.Verify(a => a.UserChangedAsync("user_delete_attempt", ActorId, target.Id,
            It.Is<IReadOnlyDictionary<string, string>>(m => m["result"] == "success")), Times.Once);
    });

    [LocalMongoFact]
    public Task Physical_delete_preserves_loan_history_and_cannot_delete_self() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var users = db.GetCollection<User>("Users");
        var targetId = ObjectId.GenerateNewId().ToString();
        await users.InsertManyAsync([new User { Id = ActorId, Username = "staff", Role = RoleNames.Admin }, new User { Id = targetId, IsActive = false }]);
        await db.GetCollection<Loan>("Loans").InsertOneAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = targetId, BookId = ObjectId.GenerateNewId().ToString(), Status = "returned", IsReturned = true });
        await using var app = await StaffHttpQueryTests.StartApp(services =>
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(new MongoAdminUserStore(users)), Mock.Of<IAdminUserAudit>())), RoleNames.Admin);
        using var client = StaffHttpQueryTests.Client(app);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/admin/users/{targetId}/permanent")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/admin/users/{ActorId}/permanent")).StatusCode);
        Assert.Equal(2, await users.CountDocumentsAsync(FilterDefinition<User>.Empty));
    });

    [Theory]
    [InlineData(RoleNames.User)]
    [InlineData(RoleNames.Librarian)]
    public async Task Physical_delete_is_admin_only(string role)
    {
        await using var app = await StaffHttpQueryTests.StartApp(services =>
            services.AddTransient(_ => new AdminUsersController(new AdminUserService(Mock.Of<IAdminUserStore>()), Mock.Of<IAdminUserAudit>())), role);
        using var client = StaffHttpQueryTests.Client(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/admin/users/{ObjectId.GenerateNewId()}/permanent")).StatusCode);
    }
}
