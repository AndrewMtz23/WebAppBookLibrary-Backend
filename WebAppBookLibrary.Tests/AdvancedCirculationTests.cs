using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class AdvancedCirculationTests
{
    [LocalMongoFact]
    public Task Isolated_snapshot_restore_preserves_loans_pickups_inventory_and_indexes() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db); await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "backup", default);
        var name = db.DatabaseNamespace.DatabaseName + "_copy"; var restore = db.Client.GetDatabase(name);
        try {
            foreach (var collection in new[] { "Books", "Users", "Loans", "PickupReservations", "WaitlistEntries", "RenewalRequests", "CirculationHistory", "CirculationState", "Notifications", "NotificationPreferences" }) {
                var docs = await db.GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
                if (docs.Count > 0) await restore.GetCollection<BsonDocument>(collection).InsertManyAsync(docs);
            }
            var restored = new MongoDBService(restore); await restored.CreateIndexesAsync();
            Assert.True((await CirculationMaintenance.RunAsync(restored, "active", false, default)).Books.Single().Consistent);
            var original = await db.GetCollection<BsonDocument>("PickupReservations").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
            Assert.Equal(original, await restore.GetCollection<BsonDocument>("PickupReservations").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync());
            Assert.Equal((await f.Db.PickupReservations.Indexes.ListAsync()).ToList().Count, (await restored.PickupReservations.Indexes.ListAsync()).ToList().Count);
        } finally { await db.Client.DropDatabaseAsync(name); }
    });
    [LocalMongoFact]
    public Task Fifo_ties_and_worker_lease_recovery_keep_one_offer_and_one_notification() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db, 0); var second = await Reader(f.Db);
        var firstEntry = (await f.Store.JoinWaitlistAsync(f.Reader.Id, f.Book.Id, default)).Entry!;
        var secondEntry = (await f.Store.JoinWaitlistAsync(second.Id, f.Book.Id, default)).Entry!;
        await f.Db.WaitlistEntries.UpdateManyAsync(FilterDefinition<WaitlistEntry>.Empty, Builders<WaitlistEntry>.Update.Set(w => w.CreatedAt, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await f.Db.Books.UpdateOneAsync(b => b.Id == f.Book.Id, Builders<Book>.Update.Set(b => b.TotalCopies, 1).Set(b => b.AvailableCopies, 1));
        var states = db.GetCollection<BsonDocument>("CirculationWorkerState");
        await states.InsertOneAsync(new BsonDocument { { "_id", "expirations" }, { "LeaseUntil", DateTime.UtcNow.AddMinutes(1) }, { "LeaseId", "crashed-worker" } });
        var worker = new CirculationReminders(f.Db, f.Store, TimeProvider.System);
        await worker.ProcessBatchAsync(default); Assert.Equal(0, await f.Db.PickupReservations.CountDocumentsAsync(FilterDefinition<PickupReservation>.Empty));
        await states.UpdateOneAsync(new BsonDocument("_id", "expirations"), new BsonDocument("$set", new BsonDocument("LeaseUntil", DateTime.UtcNow.AddSeconds(-1))));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => worker.ProcessBatchAsync(default)));
        var ready = await f.Db.PickupReservations.Find(p => p.Status == "ready").SingleAsync();
        Assert.Equal(firstEntry.Id, ready.WaitlistEntryId); Assert.NotEqual(secondEntry.Id, ready.WaitlistEntryId);
        Assert.Equal(1, await db.GetCollection<Notification>("Notifications").CountDocumentsAsync(n => n.Type == "pickup_ready"));
    });
    [LocalMongoFact]
    public Task Collection_at_deadline_cannot_beat_expiration_and_inventory_is_reconciled() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db); var p = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "one", default)).Reservation!;
        var deadline = DateTime.UtcNow.AddSeconds(-1);
        await f.Db.PickupReservations.UpdateOneAsync(x => x.Id == p.Id, Builders<PickupReservation>.Update.Set(x => x.PickupExpiresAt, deadline));
        var collecting = f.Store.CollectPickupReservationAsync(p.Id, "staff", 1, default);
        var expiring = f.Store.ProcessExpiredPickupReservationsAsync(DateTime.UtcNow, default);
        await Task.WhenAll(collecting, expiring); Assert.False(collecting.Result.Success);
        Assert.Equal(0, await f.Db.Loans.CountDocumentsAsync(FilterDefinition<Loan>.Empty));
        Assert.True((await CirculationMaintenance.RunAsync(f.Db, "active", false, default)).Books.Single().Consistent);
    });
    [LocalMongoFact]
    public Task Inconsistent_inventory_blocks_new_reservations_without_silent_repair() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        await f.Db.Books.UpdateOneAsync(b => b.Id == f.Book.Id, Builders<Book>.Update.Set(b => b.RetainedCopies, 1));
        Assert.Equal("inventory_reconciliation_required", (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "one", default)).ErrorCode);
        Assert.Equal(0, await f.Db.PickupReservations.CountDocumentsAsync(FilterDefinition<PickupReservation>.Empty));
    });
    [LocalMongoFact]
    public Task Queued_physical_relations_prevent_conversion_to_digital() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db, 0); await f.Store.JoinWaitlistAsync(f.Reader.Id, f.Book.Id, default);
        var book = await f.Db.Books.Find(b => b.Id == f.Book.Id).SingleAsync(); var version = book.UpdatedAt;
        book.MediaType = "digital"; book.TotalCopies = null; book.AvailableCopies = null; book.DigitalResourceUrl = "https://example.invalid/read";
        Assert.False(await new MongoBookStore(f.Db).ReplaceMetadataAsync(book, version, default));
    });
    [LocalMongoFact]
    public Task Creation_retries_survive_drain_and_preserve_waitlist_and_renewal_ids() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var p = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "pickup", default)).Reservation!;
        var loan = (await f.Store.CollectPickupReservationAsync(p.Id, "staff", 1, default)).Loan!;
        var r = (await f.Store.RequestRenewalAsync(loan.Id, f.Reader.Id, "More time", default, "renew")).Request!;
        var second = await Reader(f.Db);
        var w = (await f.Store.JoinWaitlistAsync(second.Id, f.Book.Id, default, "queue")).Entry!;
        await db.GetCollection<BsonDocument>("CirculationState").UpdateOneAsync(new BsonDocument("_id", "policy"), new BsonDocument("$set", new BsonDocument("Mode", "draining")));
        Assert.Equal(p.Id, (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "pickup", default)).Reservation!.Id);
        Assert.Equal(w.Id, (await f.Store.JoinWaitlistAsync(second.Id, f.Book.Id, default, "queue")).Entry!.Id);
        Assert.Equal(r.Id, (await f.Store.RequestRenewalAsync(loan.Id, f.Reader.Id, "More time", default, "renew")).Request!.Id);
        Assert.Equal("idempotency_conflict", (await f.Store.RequestRenewalAsync(loan.Id, f.Reader.Id, "Changed", default, "renew")).ErrorCode);
    });
    [LocalMongoFact]
    public Task Migration_dry_run_preserves_dates_and_activation_rejects_inventory_drift() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), UserId = f.Reader.Id, BookId = f.Book.Id, MediaType = "physical", Status = "active", DueAt = DateTime.UtcNow.AddDays(4) };
        await f.Db.Loans.InsertOneAsync(loan);
        var before = await db.GetCollection<BsonDocument>("Loans").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.False((await CirculationMaintenance.RunAsync(f.Db, "active", false, default)).Books.Single().Consistent);
        Assert.Equal(0, await db.GetCollection<BsonDocument>("CirculationState").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CirculationMaintenance.RunAsync(f.Db, "active", true, default));
        Assert.Equal(before, await db.GetCollection<BsonDocument>("Loans").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync());
        await f.Db.Books.UpdateOneAsync(b => b.Id == f.Book.Id, Builders<Book>.Update.Set(b => b.AvailableCopies, 0));
        Assert.True((await CirculationMaintenance.RunAsync(f.Db, "active", true, default)).Applied);
        var after = await f.Db.Loans.Find(l => l.Id == loan.Id).SingleAsync(); Assert.Equal("legacy", after.PolicyVersion); Assert.Equal(before["DueAt"].ToUniversalTime(), after.DueAt);
        Assert.Equal("active", await f.Store.GetModeAsync(default));
        Assert.True((await CirculationMaintenance.RunAsync(f.Db, "draining", true, default)).Applied);
    });
    [LocalMongoFact]
    public Task Persisted_drain_mode_overrides_an_active_instance() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        await db.GetCollection<BsonDocument>("CirculationState").InsertOneAsync(new BsonDocument { { "_id", "policy" }, { "Mode", "draining" }, { "Version", 1 } });
        Assert.False((await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "one", default)).Success);
        Assert.False((await f.Store.JoinWaitlistAsync(f.Reader.Id, f.Book.Id, default)).Success);
    });
    [LocalMongoFact]
    public Task Expiration_is_repeatable_and_invalidates_pickup_mail() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db); var second = await Reader(f.Db);
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.Reader.Id, Builders<User>.Update.Set(u => u.EmailVerifiedAt, DateTime.UtcNow));
        await new NotificationService(f.Db, TimeProvider.System).SavePreferencesAsync(f.Reader.Id, new(true, true), default);
        var pickup = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "one", default)).Reservation!;
        await f.Store.JoinWaitlistAsync(second.Id, f.Book.Id, default);
        var notice = await db.GetCollection<Notification>("Notifications").Find(n => n.Type == "pickup_ready" && n.UserId == f.Reader.Id).SingleAsync();
        await f.Db.PickupReservations.UpdateOneAsync(p => p.Id == pickup.Id, Builders<PickupReservation>.Update.Set(p => p.PickupExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        Assert.False((await f.Store.CollectPickupReservationAsync(pickup.Id, "staff", 1, default)).Success);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Store.ProcessExpiredPickupReservationsAsync(DateTime.UtcNow, default)));
        Assert.Equal(1, await f.Db.PickupReservations.CountDocumentsAsync(p => p.Status == "ready"));
        Assert.Null(await NotificationMail.PrepareAsync(f.Db, notice.Id, DateTime.UtcNow, "http://localhost", new(), default));
    });
    [LocalMongoFact]
    public Task Catalog_edits_and_permanent_deletion_preserve_circulation_references() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db); var books = new MongoBookStore(f.Db);
        var p = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "one", default)).Reservation!;
        var current = await f.Db.Books.Find(b => b.Id == f.Book.Id).SingleAsync();
        var edited = await f.Db.Books.Find(b => b.Id == f.Book.Id).SingleAsync(); edited.RetainedCopies = null; edited.Title = "Edited";
        Assert.True(await books.ReplaceMetadataAsync(edited, current.UpdatedAt, default));
        Assert.Equal(1, (await f.Db.Books.Find(b => b.Id == f.Book.Id).SingleAsync()).RetainedCopies);
        await books.SetActiveAsync(f.Book.Id, false, DateTime.UtcNow, default);
        Assert.False((await books.DeletePermanentlyAsync(f.Book.Id, default)).Success);
    });
    [LocalMongoFact]
    public Task Old_loan_routes_cannot_bypass_pickup_or_release_a_collected_copy() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var service = new LoanService(new MongoLoanStore(f.Db), f.Store);
        var result = await service.ReserveAsync(f.Book.Id, f.Reader.Username, f.Reader.Username, DateTime.UtcNow, default);
        Assert.Equal("physical_pickup_required", result.ErrorCode);
        var pickup = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "one", default)).Reservation!;
        var loan = (await f.Store.CollectPickupReservationAsync(pickup.Id, "staff", 1, default)).Loan!;
        Assert.False((await service.ReturnReservationAsync(loan.Id, f.Reader.Username, "user", DateTime.UtcNow, default)).Success);
        Assert.False((await service.CancelReservationAsync(loan.Id, f.Reader.Username, "user", DateTime.UtcNow, default)).Success);
        Assert.False(await new MongoLoanStore(f.Db).DeleteLoanAsync(loan.Id));
        Assert.False((await service.DeleteLoanAsync(loan.Id)).Success);
        Assert.False((await f.Db.Books.Find(b => b.Id == f.Book.Id).SingleAsync()).IsAvailable);
    });
    [LocalMongoFact]
    public Task Idempotency_survives_cancellation_and_rejects_reused_content() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var p = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "stable-key", default)).Reservation!;
        await f.Store.CancelPickupReservationAsync(p.Id, f.Reader.Id, false, null, 1, default);
        var repeat = await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "stable-key", default);
        Assert.Equal(p.Id, repeat.Reservation!.Id);
        Assert.Equal("cancelled", repeat.Reservation.Status);
        Assert.Equal("idempotency_conflict", (await f.Store.ReserveForPickupAsync(f.Reader.Id, ObjectId.GenerateNewId().ToString(), "stable-key", default)).ErrorCode);
    });
    [LocalMongoFact]
    public Task Worker_promotes_added_stock_and_drain_releases_without_promoting() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db, 0); var second = await Reader(f.Db);
        await f.Store.JoinWaitlistAsync(f.Reader.Id, f.Book.Id, default);
        await f.Store.JoinWaitlistAsync(second.Id, f.Book.Id, default);
        await f.Db.Books.UpdateOneAsync(b => b.Id == f.Book.Id, Builders<Book>.Update.Set(b => b.TotalCopies, 1).Set(b => b.AvailableCopies, 1));
        var worker = new CirculationReminders(f.Db, f.Store, TimeProvider.System);
        await worker.ProcessBatchAsync(default);
        var p = await f.Db.PickupReservations.Find(p => p.Status == "ready").SingleOrDefaultAsync();
        Assert.NotNull(p); Assert.Equal(f.Reader.Id, p.UserId);
        await db.GetCollection<BsonDocument>("CirculationState").UpdateOneAsync(new BsonDocument("_id", "policy"), new BsonDocument("$set", new BsonDocument("Mode", "draining")));
        await f.Store.CancelPickupReservationAsync(p.Id, f.Reader.Id, false, null, 1, default);
        Assert.Equal(0, await f.Db.PickupReservations.CountDocumentsAsync(p => p.Status == "ready"));
        Assert.Equal(1, (await f.Db.Books.Find(b => b.Id == f.Book.Id).SingleAsync()).AvailableCopies);
    });
    internal static async Task<(MongoDBService Db, MongoCirculationStore Store, User Reader, Book Book)> Seed(IMongoDatabase db, int copies = 1)
    {
        var f = await ReadingPersistenceTests.Seed(db);
        await f.Db.Books.UpdateOneAsync(b => b.Id == f.Book.Id, Builders<Book>.Update.Set(b => b.MediaType, "physical").Set(b => b.TotalCopies, copies).Set(b => b.AvailableCopies, copies));
        return (f.Db, new MongoCirculationStore(f.Db, Options.Create(new CirculationOptions { Mode = "active" })), f.User, f.Book);
    }
    internal static async Task<User> Reader(MongoDBService db)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var u = new User { Id = id, Username = id, Email = id + "@example.invalid", IsActive = true, Role = "user" };
        await db.Users.InsertOneAsync(u); return u;
    }
    [LocalMongoFact]
    public Task Return_offers_fifo_and_counts_the_new_retention_exactly_once() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db); var second = await Reader(f.Db);
        var pickup = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "first", default)).Reservation!;
        var collect = await f.Store.CollectPickupReservationAsync(pickup.Id, "staff", 1, default);
        Assert.True(collect.Success);
        Assert.Equal(14, (collect.Loan!.DueAt!.Value - collect.Loan.CheckedOutAt!.Value).TotalDays);
        var queued = await f.Store.JoinWaitlistAsync(second.Id, f.Book.Id, default); Assert.True(queued.Success);
        Assert.True(await f.Store.ReturnPhysicalLoanAsync(collect.Loan.Id, "staff", default));
        var book = await f.Db.Books.Find(b => b.Id == f.Book.Id).SingleAsync();
        Assert.Equal(0, book.AvailableCopies); Assert.Equal(1, book.RetainedCopies);
        Assert.Equal(second.Id, (await f.Db.PickupReservations.Find(p => p.Status == "ready").SingleAsync()).UserId);
        Assert.True(await f.Store.ReturnPhysicalLoanAsync(collect.Loan.Id, "staff", default));
        Assert.Equal(1, await f.Db.PickupReservations.CountDocumentsAsync(p => p.Status == "ready"));
    });
    [LocalMongoFact]
    public Task Last_copy_and_double_collection_have_a_single_winner() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db); var second = await Reader(f.Db);
        var results = await Task.WhenAll(new[] { f.Reader.Id, second.Id }.Select(id => f.Store.ReserveForPickupAsync(id, f.Book.Id, id, default)));
        var pickup = Assert.Single(results, r => r.Success).Reservation!;
        var collects = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => f.Store.CollectPickupReservationAsync(pickup.Id, "staff", 1, default)));
        Assert.All(collects, c => Assert.True(c.Success)); Assert.Single(collects.Select(c => c.Loan!.Id).Distinct());
        Assert.Equal(1, await f.Db.Loans.CountDocumentsAsync(FilterDefinition<Loan>.Empty));
    });
    [LocalMongoFact]
    public Task Inactive_reader_can_return_but_cannot_receive_waitlist_offer() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db); var second = await Reader(f.Db); var third = await Reader(f.Db);
        var p = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "one", default)).Reservation!;
        var loan = (await f.Store.CollectPickupReservationAsync(p.Id, "staff", 1, default)).Loan!;
        await f.Store.JoinWaitlistAsync(second.Id, f.Book.Id, default); await f.Store.JoinWaitlistAsync(third.Id, f.Book.Id, default);
        await f.Db.Users.UpdateManyAsync(u => u.Id == f.Reader.Id || u.Id == second.Id, Builders<User>.Update.Set(u => u.IsActive, false));
        Assert.True(await f.Store.ReturnPhysicalLoanAsync(loan.Id, "staff", default));
        Assert.Equal(third.Id, (await f.Db.PickupReservations.Find(p => p.Status == "ready").SingleAsync()).UserId);
    });
    [LocalMongoFact]
    public Task Renewal_revalidates_due_date_and_rejection_preserves_it() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var p = (await f.Store.ReserveForPickupAsync(f.Reader.Id, f.Book.Id, "one", default)).Reservation!;
        var loan = (await f.Store.CollectPickupReservationAsync(p.Id, "staff", 1, default)).Loan!;
        var request = (await f.Store.RequestRenewalAsync(loan.Id, f.Reader.Id, null, default)).Request!;
        var changedDue = request.OriginalDueAt.AddDays(1);
        await f.Db.Loans.UpdateOneAsync(l => l.Id == loan.Id, Builders<Loan>.Update.Set(l => l.DueAt, changedDue));
        Assert.False((await f.Store.DecideRenewalAsync(request.Id, "staff", true, null, 1, default)).Success);
        Assert.True((await f.Store.DecideRenewalAsync(request.Id, "staff", false, "Changed loan", 1, default)).Success);
        Assert.Equal(changedDue, (await f.Db.Loans.Find(l => l.Id == loan.Id).SingleAsync()).DueAt);
    });
}
