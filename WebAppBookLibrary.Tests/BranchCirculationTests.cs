using Microsoft.Extensions.Options;
using MongoDB.Driver;
using WebAppBookLibrary.Configuration;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Branches;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Tests;

public sealed class BranchCirculationTests
{
    [LocalMongoFact]
    public Task Concurrent_reservations_in_different_branches_have_only_one_winner() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db); var book = (await CopyPersistenceTests.Seed(db)).Book;
        await f.Mongo.Books.UpdateOneAsync(b => b.Id == book.Id, Builders<Book>.Update.Set(b => b.InventoryBranchId, f.A.Id));
        var catalog = new BranchCatalogStore(f.Mongo, TimeProvider.System);
        var copies = new CopyStore(f.Mongo, TimeProvider.System);
        foreach (var branch in new[] { f.A, f.B })
        {
            Assert.True((await catalog.SetAsync(f.Admin.Id, branch.Id, book.Id, true, 0, default)).Success);
            Assert.True((await copies.CreateAsync(book.Id, new(branch.Code + "-RACE", "good", null, null, branch.Code), new(f.Admin.Id, true, branch.Id), default)).Success);
        }
        var reader = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var store = new MongoCirculationStore(f.Mongo, Options.Create(new CirculationOptions { Mode = "active" }));
        var attempts = await Task.WhenAll(new[] { f.A, f.B }.Select(branch => store.ReserveForPickupAsync(reader.Id, book.Id, branch.Code, default, branch.Id)));
        Assert.Single(attempts, r => r.Success);
        Assert.Equal(1, await f.Mongo.PickupReservations.CountDocumentsAsync(p => p.UserId == reader.Id && p.Status == "ready"));
        Assert.Equal(1, (await f.Mongo.Books.Find(b => b.Id == book.Id).SingleAsync()).AvailableCopies);
    });

    [LocalMongoFact]
    public Task Reservations_allocate_local_units_keep_global_counts_and_never_duplicate_across_branches() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db); var b = (await CopyPersistenceTests.Seed(db)).Book;
        await f.Mongo.Books.UpdateOneAsync(x => x.Id == b.Id, Builders<Book>.Update.Set(x => x.InventoryBranchId, f.A.Id));
        var catalog = new BranchCatalogStore(f.Mongo, TimeProvider.System);
        await catalog.SetAsync(f.Admin.Id, f.A.Id, b.Id, true, 0, default); await catalog.SetAsync(f.Admin.Id, f.B.Id, b.Id, true, 0, default);
        var copies = new CopyStore(f.Mongo, TimeProvider.System);
        var a = (await copies.CreateAsync(b.Id, new("A-001", "good", null, null, "a"), new(f.Admin.Id, true, f.A.Id), default)).Copy!;
        var bb = (await copies.CreateAsync(b.Id, new("B-001", "good", null, null, "b"), new(f.Admin.Id, true, f.B.Id), default)).Copy!;
        var reader = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var other = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        var store = new MongoCirculationStore(f.Mongo, Options.Create(new CirculationOptions { Mode = "active" }));
        Assert.Equal("branch_required", (await store.ReserveForPickupAsync(reader.Id, b.Id, "missing", default)).ErrorCode);
        var p = await store.ReserveForPickupAsync(reader.Id, b.Id, "reserve", default, f.B.Id);
        Assert.True(p.Success); Assert.Equal(bb.Id, p.Reservation!.CopyId); Assert.Equal(f.B.Id, p.Reservation.BranchId);
        Assert.Equal("idempotency_conflict", (await store.ReserveForPickupAsync(reader.Id, b.Id, "reserve", default, f.A.Id)).ErrorCode);
        Assert.False((await store.ReserveForPickupAsync(reader.Id, b.Id, "another", default, f.A.Id)).Success);
        var wait = await store.JoinWaitlistAsync(other.Id, b.Id, default, "wait-b", f.B.Id);
        Assert.True(wait.Success); Assert.False(wait.ImmediateOffer);
        Assert.Equal(1, (await f.Mongo.Books.Find(x => x.Id == b.Id).SingleAsync()).AvailableCopies);
        Assert.Equal("available", (await db.GetCollection<BookCopy>("BookCopies").Find(c => c.Id == a.Id).SingleAsync()).Status);
        Assert.False((await store.CollectPickupReservationAsync(p.Reservation.Id, f.Admin.Id, 1, default, new(bb.Id, 2), f.A.Id)).Success);
        var collected = await store.CollectPickupReservationAsync(p.Reservation.Id, f.Admin.Id, 1, default, new(bb.Id, 2), f.B.Id);
        Assert.True(collected.Success); Assert.Equal(f.B.Id, collected.Loan!.BranchId);
        Assert.False(await store.ReturnPhysicalLoanAsync(collected.Loan.Id, f.Admin.Id, default, request: new(new(bb.Id, 3), new("good", null, null), "return"), branchId: f.A.Id));
        Assert.True(await store.ReturnPhysicalLoanAsync(collected.Loan.Id, f.Admin.Id, default, request: new(new(bb.Id, 3), new("good", null, null), "return"), branchId: f.B.Id));
        var offered = await f.Mongo.PickupReservations.Find(x => x.Status == "ready").SingleAsync();
        Assert.Equal(other.Id, offered.UserId); Assert.Equal(f.B.Id, offered.BranchId); Assert.Equal(bb.Id, offered.CopyId);
        Assert.Equal("available", (await db.GetCollection<BookCopy>("BookCopies").Find(c => c.Id == a.Id).SingleAsync()).Status);
        Assert.True((await CirculationMaintenance.RunAsync(f.Mongo, "active", false, default)).Books.Single().Consistent);
    });
}
