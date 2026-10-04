using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using WebAppBookLibrary.Configuration;

namespace WebAppBookLibrary.Tests;

public sealed class NotificationTests
{
    [LocalMongoFact]
    public Task Reminders_resume_deduplicate_choose_current_threshold_and_skip_closed_loans() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var now = DateTime.UtcNow;
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = "physical", Status = "active", DueAt = now.AddHours(10), ReservedAt = now.AddDays(-13) };
        await f.Db.Loans.InsertOneAsync(loan);
        var reminders = new NotificationReminders(f.Db, TimeProvider.System, Options.Create(new NotificationOptions()));
        await reminders.ProcessBatchAsync(default);
        await reminders.ProcessBatchAsync(default);
        var notices = db.GetCollection<Notification>("Notifications");
        Assert.Equal("due_soon", (await notices.Find(FilterDefinition<Notification>.Empty).SingleAsync()).Type);
        await f.Db.Loans.UpdateOneAsync(l => l.Id == loan.Id, Builders<Loan>.Update.Set(l => l.DueAt, now.AddDays(-1)));
        await reminders.ProcessBatchAsync(default);
        Assert.Equal(2, await notices.CountDocumentsAsync(FilterDefinition<Notification>.Empty));
        await f.Db.Loans.UpdateOneAsync(l => l.Id == loan.Id, Builders<Loan>.Update.Set(l => l.Status, "returned").Set(l => l.IsReturned, true));
        await reminders.ProcessBatchAsync(default);
        Assert.Equal(2, await notices.CountDocumentsAsync(FilterDefinition<Notification>.Empty));
    });

    [LocalMongoFact]
    public Task Optional_mail_retries_with_same_id_and_revalidates_preferences() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var service = new NotificationService(f.Db, TimeProvider.System);
        Assert.False(await service.SavePreferencesAsync(f.User.Id, new(true, true), default));
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.User.Id, Builders<User>.Update.Set(u => u.EmailVerifiedAt, DateTime.UtcNow));
        Assert.True(await service.SavePreferencesAsync(f.User.Id, new(true, true), default));
        await new MongoLoanStore(f.Db).InsertLoanAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = f.Book.MediaType, Status = "active", ReservedAt = DateTime.UtcNow }, default);
        var transport = new RecordingTransport();
        var dispatcher = new AccountMailDispatcher(f.Db, new EphemeralDataProtectionProvider(), Options.Create(new AccountRecoveryOptions { Enabled = true }), TimeProvider.System, transport);
        Assert.True(await dispatcher.ProcessNextAsync(default));
        Assert.Single(transport.Ids);
        var jobs = db.GetCollection<AccountMailJob>("AccountMailOutbox");
        var job = await jobs.Find(FilterDefinition<AccountMailJob>.Empty).SingleAsync();
        Assert.Equal("delivery", job.Stage);
        await jobs.UpdateOneAsync(j => j.Id == job.Id, Builders<AccountMailJob>.Update.Set(j => j.AvailableAt, DateTime.UtcNow.AddMinutes(-1)));
        transport.Fail = false;
        await dispatcher.ProcessNextAsync(default);
        Assert.Equal(2, transport.Ids.Count); Assert.Single(transport.Ids.Distinct());
        Assert.Equal("done", (await jobs.Find(j => j.Id == job.Id).SingleAsync()).Stage);
    });
    private sealed class RecordingTransport : IAccountMailTransport {
        public List<string> Ids { get; } = []; public bool Fail = true;
        public Task DeliverAsync(string id, AccountMailPayload message, CancellationToken ct) { Ids.Add(id); Assert.Equal("notification", message.Purpose); if (Fail) throw new IOException("provider down"); return Task.CompletedTask; }
    }

    [LocalMongoFact]
    public Task Cursor_and_read_boundary_are_private_and_exclude_later_commits() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var store = new MongoLoanStore(f.Db);
        async Task Create() {
            var book = new Book { Id = ObjectId.GenerateNewId().ToString(), IsActive = true }; await f.Db.Books.InsertOneAsync(book);
            await store.InsertLoanAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = book.Id, MediaType = book.MediaType, Status = "active", ReservedAt = DateTime.UtcNow }, default);
        }
        await Create(); await Create();
        var service = new NotificationService(f.Db, TimeProvider.System);
        var first = await service.ListAsync(f.User.Id, null, 1, null, default);
        Assert.Single(first.Items); Assert.Equal(2, first.UnreadCount); Assert.NotNull(first.NextCursor);
        await Create();
        Assert.False(await service.ReadAsync(ObjectId.GenerateNewId().ToString(), first.Items[0].Id, default));
        await service.ReadAllAsync(f.User.Id, first.ReadThrough, default);
        Assert.Equal(1, await service.UnreadCountAsync(f.User.Id, default));
        var second = await service.ListAsync(f.User.Id, first.NextCursor, 1, null, default);
        Assert.Single(second.Items); Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.NotNull(second.Items[0].ReadAt);
        Assert.Empty((await service.ListAsync(ObjectId.GenerateNewId().ToString(), null, 20, null, default)).Items);
    });

    [LocalMongoFact]
    public Task Returns_and_cancellations_are_atomic_and_idempotent() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db); var store = new MongoLoanStore(f.Db);
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = f.Book.MediaType, Status = "active", ReservedAt = DateTime.UtcNow };
        await store.InsertLoanAsync(loan, default);
        Assert.True(await store.TransitionAsync(loan.Id, ["active"], "cancelled", DateTime.UtcNow, default));
        Assert.False(await store.TransitionAsync(loan.Id, ["active"], "cancelled", DateTime.UtcNow, default));
        Assert.Equal(2, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(FilterDefinition<Notification>.Empty));
    });

    [LocalMongoFact]
    public Task Confirmed_reservation_creates_one_private_notice_and_failed_duplicate_does_not() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await ReadingPersistenceTests.Seed(db);
        var store = new MongoLoanStore(f.Db);
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.User.Id, BookId = f.Book.Id, MediaType = f.Book.MediaType, Status = "active", ReservedAt = DateTime.UtcNow };
        await store.InsertLoanAsync(loan, default);
        var notices = db.GetCollection<BsonDocument>("Notifications");
        Assert.Equal(1, await notices.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await Assert.ThrowsAnyAsync<Exception>(() => store.InsertLoanAsync(loan, default));
        Assert.Equal(1, await notices.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var notice = await notices.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal(f.User.Id, notice["UserId"].AsString);
        Assert.Equal("reserved", notice["Type"].AsString);
    });
}
