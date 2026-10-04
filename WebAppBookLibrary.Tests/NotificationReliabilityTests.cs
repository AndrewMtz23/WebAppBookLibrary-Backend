using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class NotificationReliabilityTests
{
    [LocalMongoFact]
    public Task Reminder_checkpoint_resumes_after_restart_and_expired_lease() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new TestClock();
        var loans = Enumerable.Range(0, 101).Select(i => new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id,
            MediaType = i < 100 ? "digital" : "physical", Status = "active", DueAt = i < 100 ? null : clock.GetUtcNow().UtcDateTime.AddHours(-2) }).ToArray();
        await f.Db.Loans.InsertManyAsync(loans);
        NotificationReminders Worker() => new(f.Db, clock, Options.Create(new NotificationOptions()));
        await Worker().ProcessBatchAsync(default);
        var states = db.GetCollection<BsonDocument>("NotificationWorkerState");
        Assert.Equal(loans[99].Id, (await states.Find(new BsonDocument("_id", "reminders")).SingleAsync())["Cursor"].AsString);
        await states.UpdateOneAsync(new BsonDocument("_id", "reminders"), new BsonDocument("$set", new BsonDocument("LeaseUntil", clock.GetUtcNow().UtcDateTime.AddMinutes(1))));
        await Worker().ProcessBatchAsync(default);
        Assert.Equal(0, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(FilterDefinition<Notification>.Empty));
        clock.Advance(); await Worker().ProcessBatchAsync(default);
        var notice = await db.GetCollection<Notification>("Notifications").Find(FilterDefinition<Notification>.Empty).SingleAsync();
        Assert.Equal(loans[100].Id, notice.LoanId); Assert.Equal("due_overdue", notice.Type);
    });

    [LocalMongoFact]
    public Task Notification_lease_and_lost_ack_resume_without_duplicate_local_delivery() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var clock = new TestClock();
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.User.Id, Builders<User>.Update.Set(u => u.EmailVerifiedAt, clock.GetUtcNow().UtcDateTime));
        await new NotificationService(f.Db, clock).SavePreferencesAsync(f.User.Id, new(true, true), default);
        await new MongoLoanStore(f.Db).InsertLoanAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = f.Book.MediaType, Status = "active", ReservedAt = clock.GetUtcNow().UtcDateTime }, default);
        var path = Path.Combine(Path.GetTempPath(), "booklibrary-notification-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try {
            var options = Options.Create(new AccountRecoveryOptions { Enabled = true, LocalMailDirectory = path });
            var transport = new LocalAccountMailTransport(options); var jobs = db.GetCollection<AccountMailJob>("AccountMailOutbox");
            await jobs.UpdateOneAsync(FilterDefinition<AccountMailJob>.Empty, Builders<AccountMailJob>.Update.Set(j => j.LeaseUntil, clock.GetUtcNow().UtcDateTime.AddMinutes(1)).Set(j => j.LeaseId, "crashed"));
            AccountMailDispatcher Dispatcher(IAccountMailTransport sender) => new(f.Db, new EphemeralDataProtectionProvider(), options, clock, sender);
            Assert.False(await Dispatcher(transport).ProcessNextAsync(default));
            clock.Advance(); Assert.True(await Dispatcher(new LostAck(transport)).ProcessNextAsync(default));
            Assert.Single(Directory.GetFiles(path, "*.json"));
            clock.Advance(); Assert.True(await Dispatcher(transport).ProcessNextAsync(default));
            Assert.Single(Directory.GetFiles(path, "*.json"));
            Assert.Equal("done", (await jobs.Find(FilterDefinition<AccountMailJob>.Empty).SingleAsync()).Stage);
            Assert.Equal(1, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(FilterDefinition<Notification>.Empty));
        } finally { foreach (var file in Directory.GetFiles(path)) File.Delete(file); Directory.Delete(path); }
    });
    private sealed class TestClock : TimeProvider {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now = now.AddMinutes(2);
    }
    private sealed class LostAck(IAccountMailTransport inner) : IAccountMailTransport {
        public async Task DeliverAsync(string id, AccountMailPayload message, CancellationToken ct) { await inner.DeliverAsync(id, message, ct); throw new IOException("lost acknowledgement"); }
    }

    [LocalMongoFact]
    public Task Concurrent_physical_returns_retry_notification_sequence_contention() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var store = new MongoLoanStore(f.Db);
        var loans = new List<Loan>();
        for (var i = 0; i < 12; i++) {
            var book = new Book { Id = ObjectId.GenerateNewId().ToString(), MediaType = "physical", TotalCopies = 1, AvailableCopies = 0, IsActive = true };
            await f.Db.Books.InsertOneAsync(book);
            var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = book.Id, MediaType = "physical", Status = "active" };
            await f.Db.Loans.InsertOneAsync(loan); loans.Add(loan);
        }
        var returned = await Task.WhenAll(loans.Select(l => store.CompletePhysicalAsync(l.Id, l.BookId, "returned", DateTime.UtcNow, default)));
        Assert.All(returned, result => Assert.True(result));
        Assert.Equal(12, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(n => n.Type == "returned"));
        Assert.Equal(12, await f.Db.Books.CountDocumentsAsync(b => b.AvailableCopies == 1));
    });

    [LocalMongoFact]
    public Task Paused_notifications_do_not_consume_dispatch_slots_or_attempts() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var now = DateTime.UtcNow;
        var jobs = db.GetCollection<AccountMailJob>("AccountMailOutbox");
        await jobs.InsertOneAsync(new AccountMailJob { NotificationId = ObjectId.GenerateNewId().ToString(), Stage = "delivery", CreatedAt = now.AddMinutes(-10), AvailableAt = now.AddMinutes(-10), ExpiresAt = now.AddDays(1) });
        await jobs.InsertOneAsync(new AccountMailJob { Id = "security", Stage = "request", CreatedAt = now, AvailableAt = now.AddMinutes(-1), ExpiresAt = now.AddDays(1), ProtectedPayload = "invalid-request-is-retried" });
        var dispatcher = new AccountMailDispatcher(f.Db, new EphemeralDataProtectionProvider(), Options.Create(new AccountRecoveryOptions { Enabled = true }), TimeProvider.System, new NoTransport(), Options.Create(new NotificationOptions { Enabled = false }));
        Assert.True(await dispatcher.ProcessNextAsync(default));
        Assert.Equal(1, (await jobs.Find(j => j.Id == "security").SingleAsync()).Attempts);
        Assert.Equal(0, (await jobs.Find(j => j.NotificationId != null).SingleAsync()).Attempts);
    });
    private sealed class NoTransport : IAccountMailTransport { public Task DeliverAsync(string id, AccountMailPayload message, CancellationToken ct) => throw new InvalidOperationException(); }

    [Theory]
    [InlineData(73, null)] [InlineData(72, "due_early")] [InlineData(24, "due_soon")] [InlineData(0, "due_overdue")] [InlineData(-720, "due_overdue")]
    public void Thresholds_choose_only_current_window(int hours, string? expected)
    {
        var now = DateTime.UtcNow;
        Assert.Equal(expected, NotificationReminders.CurrentType(new Loan { MediaType = "physical", Status = "active", DueAt = now.AddHours(hours) }, now, new()));
    }

    [LocalMongoFact]
    public Task Disabled_generation_keeps_business_operation_and_creates_no_notice() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db);
        var store = new MongoLoanStore(f.Db, Options.Create(new NotificationOptions { Enabled = false }));
        await store.InsertLoanAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = f.Book.MediaType, Status = "active" }, default);
        Assert.Equal(1, await f.Db.Loans.CountDocumentsAsync(FilterDefinition<Loan>.Empty));
        Assert.Equal(0, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(FilterDefinition<Notification>.Empty));
    });

    [LocalMongoFact]
    public Task Obsolete_reminders_and_revoked_delivery_eligibility_never_reach_transport() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var now = DateTime.UtcNow;
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.User.Id, Builders<User>.Update.Set(u => u.EmailVerifiedAt, now));
        await new NotificationService(f.Db, TimeProvider.System).SavePreferencesAsync(f.User.Id, new(true, true), default);
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = "physical", Status = "active", DueAt = now.AddHours(1) };
        await f.Db.Loans.InsertOneAsync(loan);
        var reminders = new NotificationReminders(f.Db, TimeProvider.System, Options.Create(new NotificationOptions()));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => reminders.ProcessBatchAsync(default)));
        var notice = await db.GetCollection<Notification>("Notifications").Find(FilterDefinition<Notification>.Empty).SingleAsync();
        async Task<bool> Eligible() => await NotificationMail.PrepareAsync(f.Db, notice.Id, now, "http://localhost", new(), default) is not null;
        Assert.True(await Eligible());
        await f.Db.Loans.UpdateOneAsync(l => l.Id == loan.Id, Builders<Loan>.Update.Set(l => l.DueAt, now.AddDays(2)));
        Assert.False(await Eligible());
        await f.Db.Loans.UpdateOneAsync(l => l.Id == loan.Id, Builders<Loan>.Update.Set(l => l.DueAt, notice.DueAt).Set(l => l.Status, "returned"));
        Assert.False(await Eligible());
        await f.Db.Loans.UpdateOneAsync(l => l.Id == loan.Id, Builders<Loan>.Update.Set(l => l.Status, "active"));
        await new NotificationService(f.Db, TimeProvider.System).SavePreferencesAsync(f.User.Id, new(false, true), default);
        Assert.False(await Eligible());
        await new NotificationService(f.Db, TimeProvider.System).SavePreferencesAsync(f.User.Id, new(true, false), default);
        Assert.False(await Eligible());
        await new NotificationService(f.Db, TimeProvider.System).SavePreferencesAsync(f.User.Id, new(true, true), default);
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.User.Id, Builders<User>.Update.Set(u => u.EmailVerifiedAt, null));
        Assert.False(await Eligible());
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.User.Id, Builders<User>.Update.Set(u => u.EmailVerifiedAt, now).Set(u => u.IsActive, false));
        Assert.False(await Eligible());
    });

    [LocalMongoFact]
    public Task Failed_physical_completion_does_not_publish_confirmation() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var store = new MongoLoanStore(f.Db);
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = f.Book.MediaType, Status = "active" };
        await store.InsertLoanAsync(loan, default);
        Assert.False(await store.CompletePhysicalAsync(loan.Id, ObjectId.GenerateNewId().ToString(), "returned", DateTime.UtcNow, default));
        Assert.Equal("active", (await store.FindLoanAsync(loan.Id, default))!.Status);
        Assert.Equal(1, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(FilterDefinition<Notification>.Empty));
    });
}
