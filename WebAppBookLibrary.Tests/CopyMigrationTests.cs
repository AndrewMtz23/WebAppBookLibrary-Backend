using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Migrations;

namespace WebAppBookLibrary.Tests;
public sealed class CopyMigrationTests
{
    [LocalMongoFact]
    public Task Conversion_limit_is_atomic_and_rejects_larger_titles()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await AdvancedCirculationTests.Seed(db,501);var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");
        var migration=new CopyInventoryMigration(f.Db,TimeProvider.System);
        Assert.False((await migration.PreviewAsync(f.Book.Id,default)).Eligible);
        Assert.Equal(0,await db.GetCollection<BookCopy>("BookCopies").CountDocumentsAsync(FilterDefinition<BookCopy>.Empty));
        await f.Db.Books.UpdateOneAsync(b=>b.Id==f.Book.Id,Builders<Book>.Update.Set(b=>b.TotalCopies,500).Set(b=>b.AvailableCopies,500));
        var preview=await migration.PreviewAsync(f.Book.Id,default);
        Assert.True((await migration.ApplyAsync(f.Book.Id,preview.Fingerprint,admin.Id,default)).Success);
        Assert.Equal(500,await db.GetCollection<BookCopy>("BookCopies").CountDocumentsAsync(FilterDefinition<BookCopy>.Empty));
        Assert.True((await CirculationMaintenance.RunAsync(f.Db,"active",false,default)).Books.Single().Consistent);
    });

    [LocalMongoFact]
    public Task Conversion_competing_with_reservation_never_leaves_partial_assignments()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await AdvancedCirculationTests.Seed(db,1);var admin=await SupportPersistenceTests.UserAsync(f.Db,"admin");
        var migration=new CopyInventoryMigration(f.Db,TimeProvider.System);
        var preview=await migration.PreviewAsync(f.Book.Id,default);
        var apply=migration.ApplyAsync(f.Book.Id,preview.Fingerprint,admin.Id,default);
        var reserve=f.Store.ReserveForPickupAsync(f.Reader.Id,f.Book.Id,"race",default);
        await Task.WhenAll(apply,reserve);
        Assert.NotEqual(apply.Result.Success,reserve.Result.Success);
        Assert.True((await CirculationMaintenance.RunAsync(f.Db,"active",false,default)).Books.Single().Consistent);
        Assert.Equal(apply.Result.Success?1:0,await db.GetCollection<BookCopy>("BookCopies").CountDocumentsAsync(FilterDefinition<BookCopy>.Empty));
    });
    [LocalMongoFact]
    public Task Conversion_preserves_commitments_marks_unknown_condition_and_is_repeatable()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await AdvancedCirculationTests.Seed(db,4);var staff=await SupportPersistenceTests.UserAsync(f.Db,"admin");
        var p=(await f.Store.ReserveForPickupAsync(f.Reader.Id,f.Book.Id,"first",default)).Reservation!;
        var loan=(await f.Store.CollectPickupReservationAsync(p.Id,staff.Id,1,default)).Loan!;
        var other=await SupportPersistenceTests.UserAsync(f.Db,"user");
        var held=(await f.Store.ReserveForPickupAsync(other.Id,f.Book.Id,"second",default)).Reservation!;
        var migration=new CopyInventoryMigration(f.Db,TimeProvider.System);
        var preview=await migration.PreviewAsync(f.Book.Id,default);Assert.True(preview.Eligible);Assert.Equal(0,preview.AvailableAfter);
        Assert.True((await migration.ApplyAsync(f.Book.Id,preview.Fingerprint,staff.Id,default)).Success);
        Assert.True((await CirculationMaintenance.RunAsync(f.Db,"active",false,default)).Books.Single().Consistent);
        var copies=await db.GetCollection<BookCopy>("BookCopies").Find(c=>c.BookId==f.Book.Id).SortBy(c=>c.InventoryCode).ToListAsync();
        Assert.Equal(4,copies.Count);Assert.Equal(2,copies.Count(c=>c.Status=="maintenance"));Assert.All(copies,c=>{Assert.Null(c.Condition);Assert.Null(c.Location);Assert.True(c.InspectionPending);});
        Assert.NotNull((await f.Db.Loans.Find(l=>l.Id==loan.Id).SingleAsync()).CopyId);
        Assert.Equal(copies.Single(c=>c.ActiveAssignmentId==loan.Id).InventoryCode,(await f.Db.Loans.Find(l=>l.Id==loan.Id).SingleAsync()).CopyInventoryCode);
        Assert.NotNull((await f.Db.PickupReservations.Find(x=>x.Id==held.Id).SingleAsync()).CopyId);
        Assert.Null((await f.Db.PickupReservations.Find(x=>x.Id==p.Id).SingleAsync()).CopyId);
        Assert.True((await migration.ApplyAsync(f.Book.Id,preview.Fingerprint,staff.Id,default)).Success);
        Assert.Equal(copies.Select(c=>c.Id),(await db.GetCollection<BookCopy>("BookCopies").Find(c=>c.BookId==f.Book.Id).SortBy(c=>c.InventoryCode).ToListAsync()).Select(c=>c.Id));
    });

    [LocalMongoFact]
    public Task Discrepancies_and_changed_snapshots_block_conversion_without_partial_units()=>StaffReviewRegressionTests.WithDatabase(async db=>{
        var f=await AdvancedCirculationTests.Seed(db,2);var staff=await SupportPersistenceTests.UserAsync(f.Db,"admin");
        var migration=new CopyInventoryMigration(f.Db,TimeProvider.System);var preview=await migration.PreviewAsync(f.Book.Id,default);
        await f.Store.ReserveForPickupAsync(f.Reader.Id,f.Book.Id,"race",default);
        Assert.False((await migration.ApplyAsync(f.Book.Id,preview.Fingerprint,staff.Id,default)).Success);
        await f.Db.Books.UpdateOneAsync(b=>b.Id==f.Book.Id,Builders<Book>.Update.Set(b=>b.TotalCopies,99));
        Assert.False((await migration.PreviewAsync(f.Book.Id,default)).Eligible);
        Assert.Equal(0,await db.GetCollection<BookCopy>("BookCopies").CountDocumentsAsync(FilterDefinition<BookCopy>.Empty));
    });
}
