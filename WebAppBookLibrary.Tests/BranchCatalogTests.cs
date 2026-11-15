using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Tests;

public sealed class BranchCatalogTests
{
    [LocalMongoFact]
    public Task Catalog_membership_is_explicit_and_inventory_blocks_withdrawal() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        var book = (await ReadingPersistenceTests.Seed(db)).Book;
        var catalog = new BranchCatalogStore(f.Mongo, TimeProvider.System);
        Assert.True((await catalog.SetAsync(f.Staff.Id, f.A.Id, book.Id, true, 0, default)).Success);
        Assert.Equal("branch_forbidden", (await catalog.SetAsync(f.Staff.Id, f.B.Id, book.Id, true, 0, default)).ErrorCode);
        Assert.True((await catalog.SetAsync(f.Admin.Id, f.B.Id, book.Id, true, 0, default)).Success);
        Assert.Equal(2, await db.GetCollection<BranchCatalogEntry>("BranchCatalog").CountDocumentsAsync(e => e.BookId == book.Id));
        await db.GetCollection<BookCopy>("BookCopies").InsertOneAsync(new() { Id = MongoDB.Bson.ObjectId.GenerateNewId().ToString(), BookId = book.Id, BranchId = f.A.Id, InventoryCode = "CAT-1", Status = "maintenance" });
        Assert.Equal("catalog_has_inventory", (await catalog.SetAsync(f.Staff.Id, f.A.Id, book.Id, false, 1, default)).ErrorCode);
        Assert.True((await catalog.SetAsync(f.Admin.Id, f.B.Id, book.Id, false, 1, default)).Success);
        Assert.Equal(2, await db.GetCollection<BranchCatalogEntry>("BranchCatalog").CountDocumentsAsync(e => e.BookId == book.Id));
        Assert.Equal("version_conflict", (await catalog.SetAsync(f.Admin.Id, f.B.Id, book.Id, true, 1, default)).ErrorCode);
    });
}
