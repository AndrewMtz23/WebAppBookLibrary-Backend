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
    public Task Consecutive_updates_accept_returned_version_but_reject_stale_version() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var users = db.GetCollection<User>("Users");
        var actor = User(ObjectId.GenerateNewId().ToString(), "editor-admin");
        var target = User(ObjectId.GenerateNewId().ToString(), "edited-user");
        target.Role = RoleNames.User;
        await users.InsertManyAsync([actor, target]);
        var store = new MongoAdminUserStore(users);
        var initial = (await store.FindByIdAsync(target.Id, default))!;
        var command = new AdminUserUpdateCommand(target.Username, "First edit", target.Email, null,
            target.Role, true, initial.UpdatedAt);
        var at = initial.UpdatedAt.AddSeconds(1).AddTicks(1234);

        var first = await store.UpdateSafelyAsync(actor.Id, target.Id, command, at, default);
        Assert.Equal(AdminStoreMutationOutcome.Success, first.Outcome);
        Assert.NotNull(first.UpdatedUser);
        var secondCommand = command with { DisplayName = "Second edit", ExpectedUpdatedAt = first.UpdatedUser.UpdatedAt };
        var second = await store.UpdateSafelyAsync(actor.Id, target.Id, secondCommand, at.AddSeconds(1), default);

        Assert.Equal(AdminStoreMutationOutcome.Success, second.Outcome);
        Assert.NotNull(second.UpdatedUser);
        var persisted = (await store.FindByIdAsync(target.Id, default))!;
        Assert.Equal("Second edit", persisted.DisplayName);
        Assert.Equal(persisted.UpdatedAt, second.UpdatedUser.UpdatedAt);

        var stale = await store.UpdateSafelyAsync(actor.Id, target.Id,
            secondCommand with { DisplayName = "Stale edit" }, at.AddSeconds(2), default);
        Assert.Equal(AdminStoreMutationOutcome.Conflict, stale.Outcome);
        Assert.Equal("Second edit", (await store.FindByIdAsync(target.Id, default))!.DisplayName);
    });

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
