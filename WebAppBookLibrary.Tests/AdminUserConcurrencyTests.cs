using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class AdminUserConcurrencyTests
{
    [LocalMongoFact]
    [Trait("Category", "LocalMongo")]
    public Task Two_active_admins_cannot_remove_each_other_and_leave_no_active_admin() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var users = db.GetCollection<User>("Users");
        var firstId = ObjectId.GenerateNewId().ToString();
        var secondId = ObjectId.GenerateNewId().ToString();
        await users.InsertManyAsync([
            User(firstId, "admin-one"),
            User(secondId, "admin-two")
        ]);
        var firstStore = new MongoAdminUserStore(users);
        var secondStore = new MongoAdminUserStore(users);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = Task.Run(async () => { await gate.Task; return await firstStore.SetRoleSafelyAsync(firstId.ToUpperInvariant(), secondId.ToUpperInvariant(), RoleNames.User, DateTime.UtcNow, default); });
        var second = Task.Run(async () => { await gate.Task; return await secondStore.SetStatusSafelyAsync(secondId.ToUpperInvariant(), firstId.ToUpperInvariant(), false, DateTime.UtcNow, default); });
        gate.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, result => result.Outcome == AdminStoreMutationOutcome.Success);
        Assert.Equal(1, await users.CountDocumentsAsync(user => user.Role == RoleNames.Admin && user.IsActive));
    });

    private static User User(string id, string username) => new()
    {
        Id = id,
        Username = username,
        NormalizedUsername = username.ToUpperInvariant(),
        DisplayName = username,
        Email = $"{username}@example.test",
        NormalizedEmail = $"{username}@example.test".ToUpperInvariant(),
        Role = RoleNames.Admin,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
}
