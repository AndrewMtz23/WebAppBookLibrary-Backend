using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Configuration;

namespace WebAppBookLibrary.Tests;

public sealed class AccountRecoveryTests
{
    [LocalMongoFact]
    public Task Requests_are_generic_and_reset_is_single_use_and_revokes_credentials() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db); var clock = new TestClock(); var mail = new Inbox();
        var options = Options.Create(new AccountRecoveryOptions { Enabled = true });
        var protector = new EphemeralDataProtectionProvider();
        var service = new AccountRecoveryService(mongo, protector, options, clock);
        var dispatcher = new AccountMailDispatcher(mongo, protector, options, clock, mail);
        await service.EnsureIndexesAsync(default);
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "recovery", Email = "qa@example.invalid", NormalizedEmail = "QA@EXAMPLE.INVALID", PasswordHash = PasswordHasher.HashPassword("Original1") };
        await mongo.Users.InsertOneAsync(user);
        Assert.True(await service.RequestResetAsync(user.Email, default));
        Assert.True(await service.RequestResetAsync("missing@example.invalid", default));
        Assert.Empty(mail.Messages);
        await dispatcher.ProcessNextAsync(default); await dispatcher.ProcessNextAsync(default);
        var message = Assert.Single(mail.Messages);
        Assert.DoesNotContain(message.Token, (await db.GetCollection<AccountChallenge>("AccountChallenges").Find(FilterDefinition<AccountChallenge>.Empty).SingleAsync()).ToJson());
        Assert.False(await service.ConfirmAsync("verify", message.Token, null, default));
        var results = await Task.WhenAll(service.ConfirmAsync("reset", message.Token, "Changed1", default), service.ConfirmAsync("reset", message.Token, "Another1", default));
        Assert.Single(results, result => result);
        Assert.False(await service.ConfirmAsync("reset", message.Token, "Again1", default));
        Assert.Equal(1, (await mongo.Users.Find(u => u.Id == user.Id).SingleAsync()).CredentialVersion);
    });

    [LocalMongoFact]
    public Task Verification_is_bound_to_email_version_and_expiry_and_new_request_invalidates_old() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db); var clock = new TestClock(); var mail = new Inbox();
        var options = Options.Create(new AccountRecoveryOptions { Enabled = true }); var protector = new EphemeralDataProtectionProvider();
        var service = new AccountRecoveryService(mongo, protector, options, clock); var dispatcher = new AccountMailDispatcher(mongo, protector, options, clock, mail);
        await service.EnsureIndexesAsync(default);
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "verify", Email = "qa@example.invalid", NormalizedEmail = "QA@EXAMPLE.INVALID" };
        await mongo.Users.InsertOneAsync(user);
        await service.RequestVerificationAsync(user.Id, default); await dispatcher.ProcessNextAsync(default);
        var old = mail.Messages.Last();
        clock.Advance(TimeSpan.FromMinutes(2));
        await service.RequestVerificationAsync(user.Id, default); await dispatcher.ProcessNextAsync(default);
        Assert.False(await service.ConfirmAsync("verify", old.Token, null, default));
        var current = mail.Messages.Last();
        await mongo.Users.UpdateOneAsync(u => u.Id == user.Id, Builders<User>.Update.Inc(u => u.EmailVersion, 1));
        Assert.False(await service.ConfirmAsync("verify", current.Token, null, default));
        clock.Advance(TimeSpan.FromMinutes(2));
        await service.RequestVerificationAsync(user.Id, default); await dispatcher.ProcessNextAsync(default);
        clock.Advance(TimeSpan.FromHours(25));
        Assert.False(await service.ConfirmAsync("verify", mail.Messages.Last().Token, null, default));
    });

    [LocalMongoFact]
    public Task Failed_delivery_retries_same_token_and_request_is_throttled() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db); var clock = new TestClock(); var mail = new Inbox { Failures = 1 };
        var options = Options.Create(new AccountRecoveryOptions { Enabled = true }); var protector = new EphemeralDataProtectionProvider();
        var service = new AccountRecoveryService(mongo, protector, options, clock);
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Email = "retry@example.invalid" }; await mongo.Users.InsertOneAsync(user);
        await service.EnsureIndexesAsync(default);
        Assert.True(await service.RequestVerificationAsync(user.Id, default));
        Assert.False(await service.RequestVerificationAsync(user.Id, default));
        await new AccountMailDispatcher(mongo, protector, options, clock, mail).ProcessNextAsync(default);
        Assert.Empty(mail.Messages);
        var hash = (await db.GetCollection<AccountChallenge>("AccountChallenges").Find(c => c.UserId == user.Id).SingleAsync()).TokenHash;
        clock.Advance(TimeSpan.FromMinutes(1));
        // A new dispatcher simulates restart; the encrypted delivery is retained.
        await new AccountMailDispatcher(mongo, protector, options, clock, mail).ProcessNextAsync(default);
        var message = Assert.Single(mail.Messages); Assert.Equal(hash, AccountRecoveryService.Hash(message.Token));
        Assert.True(await service.ConfirmAsync("verify", message.Token, null, default));
        Assert.False(await service.ConfirmAsync("verify", message.Token, null, default));
        Assert.NotNull((await mongo.Users.Find(u => u.Id == user.Id).SingleAsync()).EmailVerifiedAt);
        var job = await db.GetCollection<AccountMailJob>("AccountMailOutbox").Find(FilterDefinition<AccountMailJob>.Empty).SingleAsync();
        Assert.Equal("done", job.Stage); Assert.Empty(job.ProtectedPayload);
    });

    [LocalMongoFact]
    public Task Profile_email_changes_clear_verification_and_invalidate_old_links() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db); var clock = new TestClock(); var mail = new Inbox();
        var options = Options.Create(new AccountRecoveryOptions { Enabled = true }); var protector = new EphemeralDataProtectionProvider();
        var service = new AccountRecoveryService(mongo, protector, options, clock);
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Email = "old@example.invalid", UpdatedAt = clock.GetUtcNow().UtcDateTime };
        await mongo.Users.InsertOneAsync(user);
        await service.RequestVerificationAsync(user.Id, default);
        await new AccountMailDispatcher(mongo, protector, options, clock, mail).ProcessNextAsync(default);
        var store = new MongoUserStore(mongo);
        var updated = await store.UpdateProfileAsync(user.Id, new("QA", "new@example.invalid", null, user.UpdatedAt), default);
        Assert.NotNull(updated); Assert.Equal(1, updated.EmailVersion); Assert.Null(updated.EmailVerifiedAt);
        var restored = await store.UpdateProfileAsync(user.Id, new("QA", user.Email, null, updated.UpdatedAt), default);
        Assert.Equal(2, restored!.EmailVersion);
        Assert.False(await service.ConfirmAsync("verify", Assert.Single(mail.Messages).Token, null, default));
    });

    [LocalMongoFact]
    public Task Admin_email_change_clears_verification_and_rejects_previous_link() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var mongo = new MongoDBService(db); var clock = new TestClock(); var mail = new Inbox();
        var options = Options.Create(new AccountRecoveryOptions { Enabled = true }); var protector = new EphemeralDataProtectionProvider();
        var service = new AccountRecoveryService(mongo, protector, options, clock);
        var admin = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "admin", Email = "admin@example.invalid", Role = "admin" };
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "reader", Email = "old@example.invalid", UpdatedAt = clock.GetUtcNow().UtcDateTime };
        await mongo.Users.InsertManyAsync([admin, user]);
        await service.RequestVerificationAsync(user.Id, default);
        await new AccountMailDispatcher(mongo, protector, options, clock, mail).ProcessNextAsync(default);
        var message = Assert.Single(mail.Messages);
        await mongo.Users.UpdateOneAsync(u => u.Id == user.Id, Builders<User>.Update.Set(u => u.EmailVerifiedAt, clock.GetUtcNow().UtcDateTime));
        var result = await new MongoAdminUserStore(mongo).UpdateSafelyAsync(admin.Id, user.Id,
            new(user.Username, "Reader", "new@example.invalid", null, "user", true, user.UpdatedAt), user.UpdatedAt.AddSeconds(1), default);
        Assert.Equal(AdminStoreMutationOutcome.Success, result.Outcome);
        var saved = await mongo.Users.Find(u => u.Id == user.Id).SingleAsync();
        Assert.Null(saved.EmailVerifiedAt); Assert.Equal(1, saved.EmailVersion);
        Assert.False(await service.ConfirmAsync("verify", message.Token, null, default));
    });

    private sealed class Inbox : IAccountMailTransport
    {
        public List<AccountMailPayload> Messages { get; } = [];
        public int Failures { get; set; }
        public Task DeliverAsync(string id, AccountMailPayload message, CancellationToken ct) { if (Failures-- > 0) throw new IOException("Test outage"); Messages.Add(message); return Task.CompletedTask; }
    }
    private sealed class TestClock : TimeProvider
    {
        // Keep Mongo's real TTL monitor from deleting fixtures governed by this test clock.
        private DateTimeOffset now = new(2090, 9, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
