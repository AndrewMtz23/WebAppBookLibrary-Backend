using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;
public sealed class EmailLoginTests
{
    [LocalMongoFact]
    public Task Registration_cannot_shadow_a_legacy_email() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db);
        await mongo.CreateIndexesAsync();
        var legacy = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "legacy", Email = "Legacy@Example.invalid" };
        await mongo.Users.InsertOneAsync(legacy);
        var service = new UserService(new MongoUserStore(mongo));
        var created = await service.CreateUserAsync(new(Username: "new-reader", Email: "legacy@example.invalid", Password: "Password1"));
        Assert.False(created.Success);
        Assert.Equal(UserCreationErrorCodes.DuplicateUser, created.ErrorCode);
        Assert.Equal(legacy.Id, (await service.GetUserByEmailAsync("legacy@example.invalid"))?.Id);
    });

    [LocalMongoFact]
    public Task Profile_and_admin_edits_cannot_shadow_a_legacy_email() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db);
        await mongo.CreateIndexesAsync();
        var at = new BsonDateTime(DateTime.UtcNow).ToUniversalTime();
        var legacy = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "legacy", Email = " Legacy@Example.invalid " };
        var target = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "target", Email = "target@example.invalid", UpdatedAt = at };
        var admin = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "admin", Email = "admin@example.invalid", Role = "admin" };
        await mongo.Users.InsertManyAsync([legacy, target, admin]);
        var store = new MongoUserStore(mongo);
        Assert.Null(await store.UpdateProfileAsync(target.Id, new("Target", "legacy@example.invalid", null, at), default));
        var result = await new MongoAdminUserStore(mongo).UpdateSafelyAsync(admin.Id, target.Id,
            new(target.Username, "Target", "legacy@example.invalid", null, "user", true, at), at.AddSeconds(1), default);
        Assert.Equal(AdminStoreMutationOutcome.IdentityConflict, result.Outcome);
        Assert.Equal(legacy.Id, (await store.FindByEmailAsync("legacy@example.invalid"))?.Id);
        Assert.Equal(target.Email, (await mongo.Users.Find(u => u.Id == target.Id).SingleAsync()).Email);
    });

    [LocalMongoFact]
    public Task Email_lookup_normalizes_input_supports_legacy_and_never_matches_username() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db);
        var first = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "reader", Email = "Reader@Example.test", NormalizedEmail = "READER@EXAMPLE.TEST" };
        var legacy = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "legacy", Email = "Legacy@Example.test" };
        var misleading = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "absent@example.test", Email = "different@example.test" };
        await mongo.Users.InsertManyAsync([first, legacy, misleading]);
        var service = new UserService(new MongoUserStore(mongo));
        Assert.Equal(first.Id, (await service.GetUserByEmailAsync("  reader@EXAMPLE.TEST  "))?.Id);
        Assert.Equal(legacy.Id, (await service.GetUserByEmailAsync("legacy@example.test"))?.Id);
        Assert.Null(await service.GetUserByEmailAsync("absent@example.test"));
        Assert.Null(await service.GetUserByEmailAsync("reader"));
        await mongo.Users.InsertOneAsync(new User { Id = ObjectId.GenerateNewId().ToString(), Username = "duplicate", Email = "LEGACY@example.test" });
        Assert.Null(await service.GetUserByEmailAsync("legacy@example.test"));
    });
}
