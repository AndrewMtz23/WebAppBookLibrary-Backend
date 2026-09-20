using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Profile;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class ProfileMutationTests
{
    [LocalMongoFact]
    public Task All_roles_update_only_their_identity_with_concurrency_and_unique_email_protection() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db);
        await mongo.CreateIndexesAsync();
        const string id = "507f1f77bcf86cd799439011";
        foreach (var role in new[] { "user", "librarian", "admin" })
        {
            await mongo.Users.DeleteManyAsync(FilterDefinition<User>.Empty);
            var at = new BsonDateTime(DateTime.UtcNow).ToUniversalTime();
            await mongo.Users.InsertManyAsync(new[] {
                new User { Id = id, Username = "staff", NormalizedUsername = "STAFF", Email = "staff@example.test", NormalizedEmail = "STAFF@EXAMPLE.TEST", DisplayName = "Original", Role = role, IsActive = true, UpdatedAt = at, PasswordHash = "secret-hash" },
                new User { Id = ObjectId.GenerateNewId().ToString(), Username = "other", NormalizedUsername = "OTHER", Email = "other@example.test", NormalizedEmail = "OTHER@EXAMPLE.TEST", Role = "user", IsActive = true, UpdatedAt = at }
            });
            await using var app = await StaffHttpQueryTests.StartApp(services => services.AddSingleton(new ProfileController(new ProfileService(new MongoUserStore(mongo)))), role);
            using var client = StaffHttpQueryTests.Client(app);
            var original = await client.GetFromJsonAsync<ProfileResponse>("/api/profile/me");
            Assert.Equal(role, original!.Role);
            var request = new UpdateProfileRequest("  New name  ", "new@example.test", "https://example.test/avatar.jpg", original.UpdatedAt);
            using var saved = await client.PutAsJsonAsync("/api/profile/me", request);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var result = (await saved.Content.ReadFromJsonAsync<ProfileResponse>())!;
            Assert.Equal("New name", result.DisplayName);
            Assert.True(result.UpdatedAt > original.UpdatedAt);
            Assert.DoesNotContain("secret-hash", await saved.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/profile/me", request)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/profile/me", request with { Email = "OTHER@example.test", ExpectedUpdatedAt = result.UpdatedAt })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/profile/me", request with { AvatarUrl = "http://example.test/a", ExpectedUpdatedAt = result.UpdatedAt })).StatusCode);
            foreach (var forbidden in new[] { "role", "isActive", "id", "username", "passwordHash" })
            {
                var body = new Dictionary<string, object?> { ["displayName"] = "Attack", ["email"] = "new@example.test", ["expectedUpdatedAt"] = result.UpdatedAt, [forbidden] = "admin" };
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/profile/me", body)).StatusCode);
            }
            var second = await client.PutAsJsonAsync("/api/profile/me", request with { DisplayName = "Second", AvatarUrl = null, ExpectedUpdatedAt = result.UpdatedAt });
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var stored = await mongo.Users.Find(u => u.Id == id).SingleAsync();
            Assert.Equal(role, stored.Role); Assert.True(stored.IsActive);
            Assert.Equal("staff", stored.Username); Assert.Equal("secret-hash", stored.PasswordHash);
            Assert.Equal("Second", stored.DisplayName); Assert.Null(stored.AvatarUrl);
            Assert.Equal("NEW@EXAMPLE.TEST", stored.NormalizedEmail);
            Assert.Equal("other@example.test", (await mongo.Users.Find(u => u.Username == "other").SingleAsync()).Email);
            await mongo.Users.UpdateOneAsync(u => u.Id == id, Builders<User>.Update.Set(u => u.IsActive, false));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/profile/me", request with { ExpectedUpdatedAt = stored.UpdatedAt })).StatusCode);
        }
    });
}
