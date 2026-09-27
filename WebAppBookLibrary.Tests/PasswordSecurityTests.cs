using System.Security.Claims;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class PasswordSecurityTests
{
    [LocalMongoFact]
    public Task Password_change_revokes_legacy_and_versioned_sessions_and_preserves_profile() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db);
        var user = Account();
        await database.Users.InsertOneAsync(user);
        await database.Users.UpdateOneAsync(u => u.Id == user.Id, Builders<User>.Update.Unset(u => u.CredentialVersion));
        var service = new PasswordSecurityService(database);
        var store = new MongoUserStore(database);
        var legacy = Principal(user, null);
        Assert.True(await CurrentAccountValidator.ValidateAsync(legacy, store));
        Assert.Equal("current_password_invalid", await service.ChangeAsync(user.Id, 0, "Wrong1", "ChangedPassword1", default));
        Assert.Equal("password_invalid", await service.ChangeAsync(user.Id, 0, "OriginalPassword1", "weak", default));
        Assert.Null(await service.ChangeAsync(user.Id, 0, "OriginalPassword1", "ChangedPassword1", default));
        Assert.False(await CurrentAccountValidator.ValidateAsync(legacy, store));
        Assert.False(await CurrentAccountValidator.ValidateAsync(Principal(user, "0"), store));
        Assert.True(await CurrentAccountValidator.ValidateAsync(Principal(user, "1"), store));
        foreach (var value in new[] { "-1", "invalid", "1.0", "9223372036854775808" })
            Assert.False(await CurrentAccountValidator.ValidateAsync(Principal(user, value), store));
        var saved = await database.Users.Find(u => u.Id == user.Id).SingleAsync();
        Assert.Equal(1, saved.CredentialVersion);
        Assert.Equal(user.DisplayName, saved.DisplayName);
        Assert.Equal(user.Email, saved.Email);
        Assert.True(PasswordHasher.VerifyPassword("ChangedPassword1", saved.PasswordHash));
        Assert.False(PasswordHasher.VerifyPassword("OriginalPassword1", saved.PasswordHash));
        Assert.Equal("session_changed", await service.ChangeAsync(user.Id, 0, "ChangedPassword1", "AnotherPassword1", default));
    });

    [LocalMongoFact]
    public Task Two_concurrent_changes_only_increment_credentials_once() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db); var user = Account();
        await database.Users.InsertOneAsync(user);
        var service = new PasswordSecurityService(database);
        var results = await Task.WhenAll(service.ChangeAsync(user.Id, 0, "OriginalPassword1", "FirstPassword1", default),
            service.ChangeAsync(user.Id, 0, "OriginalPassword1", "SecondPassword1", default));
        Assert.Single(results, result => result is null);
        Assert.Equal(1, (await database.Users.Find(u => u.Id == user.Id).SingleAsync()).CredentialVersion);
    });

    private static User Account() => new() { Id = ObjectId.GenerateNewId().ToString(), Username = "passwordqa", DisplayName = "Preserved profile", Email = "qa@example.invalid", Role = RoleNames.User, PasswordHash = PasswordHasher.HashPassword("OriginalPassword1") };
    private static ClaimsPrincipal Principal(User user, string? version)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, user.Username), new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Role, user.Role) };
        if (version is not null) claims.Add(new("credential_version", version));
        return new(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role));
    }
}
