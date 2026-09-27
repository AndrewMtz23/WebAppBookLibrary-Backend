using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class AccountMailReliabilityTests
{
    [LocalMongoFact]
    public Task Concurrent_requests_and_workers_produce_one_local_message() => WithMailbox(async (db, path) =>
    {
        var fixture = await Fixture.Create(db, path);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Service.RequestResetAsync(" QA@EXAMPLE.INVALID ", default)));
        Assert.All(results, result => Assert.True(result));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.Dispatcher().ProcessNextAsync(default)));
        Assert.Single(Directory.GetFiles(path, "*.json"));
        Assert.Equal(1, await fixture.Jobs.CountDocumentsAsync(FilterDefinition<AccountMailJob>.Empty));
        Assert.Equal("done", (await fixture.Jobs.Find(FilterDefinition<AccountMailJob>.Empty).SingleAsync()).Stage);
    });

    [LocalMongoFact]
    public Task Exhausted_retries_stop_and_erase_payload() => WithMailbox(async (db, path) =>
    {
        var fixture = await Fixture.Create(db, path);
        await fixture.Service.RequestVerificationAsync(fixture.User.Id, default);
        var failing = new FailingTransport();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.True(await fixture.Dispatcher(failing).ProcessNextAsync(default));
            Assert.False(await fixture.Dispatcher(failing).ProcessNextAsync(default));
            fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        }
        Assert.False(await fixture.Dispatcher(failing).ProcessNextAsync(default));
        Assert.Equal(5, failing.Calls);
        var job = await fixture.Jobs.Find(FilterDefinition<AccountMailJob>.Empty).SingleAsync();
        Assert.Equal("dead", job.Stage);
        Assert.Empty(job.ProtectedPayload);
        Assert.Empty(Directory.GetFiles(path, "*.json"));
    });

    [LocalMongoFact]
    public Task Restart_with_persisted_keys_recovers_delivery_and_deduplicates_after_lost_ack() => WithMailbox(async (db, path) =>
    {
        var fixture = await Fixture.Create(db, path);
        await fixture.Service.RequestVerificationAsync(fixture.User.Id, default);
        var deliverThenFail = new LostAcknowledgement(new LocalAccountMailTransport(fixture.Options));
        await fixture.Dispatcher(deliverThenFail).ProcessNextAsync(default);
        Assert.Single(Directory.GetFiles(path, "*.json"));
        var pending = await fixture.Jobs.Find(FilterDefinition<AccountMailJob>.Empty).SingleAsync();
        Assert.Equal("delivery", pending.Stage);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var restarted = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(path, "keys")), b => b.SetApplicationName("BookLibrary.AccountSecurity"));
        await new AccountMailDispatcher(fixture.Mongo, restarted, fixture.Options, fixture.Clock, new LocalAccountMailTransport(fixture.Options)).ProcessNextAsync(default);
        var message = System.Text.Json.JsonSerializer.Deserialize<AccountMailPayload>(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(path, "*.json"))))!;
        Assert.True(await new AccountRecoveryService(fixture.Mongo, restarted, fixture.Options, fixture.Clock).ConfirmAsync("verify", message.Token, null, default));
        Assert.Equal("done", (await fixture.Jobs.Find(j => j.Id == pending.Id).SingleAsync()).Stage);
    });

    [LocalMongoFact]
    public Task Expired_lease_is_reclaimed_but_live_lease_is_not() => WithMailbox(async (db, path) =>
    {
        var fixture = await Fixture.Create(db, path);
        await fixture.Service.RequestVerificationAsync(fixture.User.Id, default);
        await fixture.Jobs.UpdateOneAsync(FilterDefinition<AccountMailJob>.Empty, Builders<AccountMailJob>.Update
            .Set(j => j.LeaseId, "crashed-worker").Set(j => j.LeaseUntil, fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(1)).Inc(j => j.Attempts, 1));
        Assert.False(await fixture.Dispatcher().ProcessNextAsync(default));
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await fixture.Dispatcher().ProcessNextAsync(default));
        Assert.Single(Directory.GetFiles(path, "*.json"));
    });

    [LocalMongoFact]
    public Task Expired_challenge_is_not_delivered_on_retry() => WithMailbox(async (db, path) =>
    {
        var fixture = await Fixture.Create(db, path);
        await fixture.Service.RequestResetAsync(fixture.User.Email, default);
        await fixture.Dispatcher(new FailingTransport()).ProcessNextAsync(default);
        fixture.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.True(await fixture.Dispatcher().ProcessNextAsync(default));
        Assert.Empty(Directory.GetFiles(path, "*.json"));
        Assert.Equal("done", (await fixture.Jobs.Find(FilterDefinition<AccountMailJob>.Empty).SingleAsync()).Stage);
    });

    [LocalMongoFact]
    public Task Inactive_account_and_disabled_service_do_not_issue_challenges() => WithMailbox(async (db, path) =>
    {
        var fixture = await Fixture.Create(db, path);
        fixture.Options.Value.Enabled = false;
        Assert.False(await fixture.Service.RequestResetAsync(fixture.User.Email, default));
        Assert.False(await fixture.Dispatcher().ProcessNextAsync(default));
        Assert.Equal(0, await fixture.Jobs.CountDocumentsAsync(FilterDefinition<AccountMailJob>.Empty));
        fixture.Options.Value.Enabled = true;
        await fixture.Mongo.Users.UpdateOneAsync(u => u.Id == fixture.User.Id, Builders<User>.Update.Set(u => u.IsActive, false));
        Assert.True(await fixture.Service.RequestResetAsync(fixture.User.Email, default));
        await fixture.Dispatcher().ProcessNextAsync(default);
        Assert.Empty(Directory.GetFiles(path, "*.json"));
        Assert.Equal(0, await db.GetCollection<AccountChallenge>("AccountChallenges").CountDocumentsAsync(FilterDefinition<AccountChallenge>.Empty));
    });

    private static Task WithMailbox(Func<IMongoDatabase, string, Task> test) => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var path = Path.Combine(Path.GetTempPath(), "booklibrary-mail-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { await test(db, path); }
        finally { Directory.Delete(path, true); }
    });

    private sealed class Fixture
    {
        public required MongoDBService Mongo { get; init; }
        public required IDataProtectionProvider Protection { get; init; }
        public required IOptions<AccountRecoveryOptions> Options { get; init; }
        public required AccountRecoveryService Service { get; init; }
        public required User User { get; init; }
        public Clock Clock { get; init; } = new();
        public IMongoCollection<AccountMailJob> Jobs => Mongo._database.GetCollection<AccountMailJob>("AccountMailOutbox");
        public AccountMailDispatcher Dispatcher(IAccountMailTransport? transport = null) => new(Mongo, Protection, Options, Clock, transport ?? new LocalAccountMailTransport(Options));
        public static async Task<Fixture> Create(IMongoDatabase db, string path)
        {
            var mongo = new MongoDBService(db);
            var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(path, "keys")), b => b.SetApplicationName("BookLibrary.AccountSecurity"));
            var options = Microsoft.Extensions.Options.Options.Create(new AccountRecoveryOptions { Enabled = true, LocalMailDirectory = path });
            var clock = new Clock();
            var service = new AccountRecoveryService(mongo, protection, options, clock);
            await service.EnsureIndexesAsync(default);
            var user = new User { Id = ObjectId.GenerateNewId().ToString(), Email = "qa@example.invalid", NormalizedEmail = "QA@EXAMPLE.INVALID", IsActive = true };
            await mongo.Users.InsertOneAsync(user);
            return new Fixture { Mongo = mongo, Protection = protection, Options = options, Clock = clock, Service = service, User = user };
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2090, 9, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
    private sealed class FailingTransport : IAccountMailTransport
    {
        public int Calls { get; private set; }
        public Task DeliverAsync(string id, AccountMailPayload message, CancellationToken ct) { Calls++; throw new IOException("Local test outage"); }
    }
    private sealed class LostAcknowledgement(IAccountMailTransport inner) : IAccountMailTransport
    {
        public async Task DeliverAsync(string id, AccountMailPayload message, CancellationToken ct)
        {
            await inner.DeliverAsync(id, message, ct);
            throw new IOException("Simulated lost acknowledgement");
        }
    }
}
