using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Moq;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class AdminUserCreationTests
{
    [LocalMongoFact]
    public Task Creates_hashed_active_accounts_without_changing_actor_and_rejects_duplicates_and_other_roles() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db); await mongo.CreateIndexesAsync();
        const string actorId = "507f1f77bcf86cd799439011";
        await mongo.Users.InsertOneAsync(new User { Id = actorId, Username = "staff", NormalizedUsername = "STAFF", Email = "staff@example.test", NormalizedEmail = "STAFF@EXAMPLE.TEST", Role = "admin", IsActive = true });
        var audit = new Mock<IAdminUserAudit>();
        audit.Setup(x => x.UserChangedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string,string>>())).Returns(Task.CompletedTask);
        var controller = new AdminUserCreationController(new MongoUserStore(mongo), audit.Object);
        var request = new CreateAdminUserRequest("newreader", "New Reader", "new@example.test", "StrongTest123!", "user");
        foreach (var role in new[] { "user", "librarian" })
        {
            await using var forbidden = await StaffHttpQueryTests.StartApp(s => s.AddSingleton(controller), role);
            using var client = StaffHttpQueryTests.Client(forbidden);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/users", request)).StatusCode);
        }
        await using var app = await StaffHttpQueryTests.StartApp(s => s.AddSingleton(controller), "admin");
        using var admin = StaffHttpQueryTests.Client(app);
        foreach (var role in new[] { "user", "librarian", "admin" })
        {
            var body = request with { Username = "new" + role, Email = role + "@example.test", Role = role };
            var response = await admin.PostAsJsonAsync("/api/admin/users", body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var safe = (await response.Content.ReadFromJsonAsync<AdminUserResponse>())!;
            Assert.Equal(role, safe.Role); Assert.True(safe.IsActive);
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("token", text, StringComparison.OrdinalIgnoreCase);
            var stored = await mongo.Users.Find(u => u.Id == safe.Id).SingleAsync();
            Assert.True(PasswordHasher.VerifyPassword(body.Password, stored.PasswordHash));
            Assert.Equal(body.Email.ToUpperInvariant(), stored.NormalizedEmail);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/users", body with { Username = body.Username.ToUpperInvariant() })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/users", request with { Password = "weak" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/users", request with { Role = "owner" })).StatusCode);
        Assert.Equal(4, await mongo.Users.CountDocumentsAsync(FilterDefinition<User>.Empty));
        Assert.Equal("admin", (await mongo.Users.Find(u => u.Id == actorId).SingleAsync()).Role);
        audit.Verify(x => x.UserChangedAsync("user_create", actorId, It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string,string>>()), Times.Exactly(3));
    });
}
