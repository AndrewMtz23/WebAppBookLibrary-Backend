using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Copies;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Branches;
using WebAppBookLibrary.Services.Copies;

namespace WebAppBookLibrary.Tests;

public sealed class BranchCopyTests
{
    [LocalMongoFact]
    public Task Legacy_copy_link_resolves_only_an_authorized_origin_without_changing_mutation_scope() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        var book = (await CopyPersistenceTests.Seed(db)).Book;
        await new BranchCatalogStore(f.Mongo, TimeProvider.System).SetAsync(f.Admin.Id, f.B.Id, book.Id, true, 0, default);
        var store = new CopyStore(f.Mongo, TimeProvider.System);
        var copy = (await store.CreateAsync(book.Id, new("QR-B", "good", null, null, "qr"), new(f.Admin.Id, true, f.B.Id), default)).Copy!;
        var queries = new CopyQueries(f.Mongo, new(f.Staff.Id, f.A.Id, true));
        Assert.Null(await queries.ResolveOriginAsync(copy.Id, f.Staff.Id, default));
        await new BranchStore(f.Mongo, TimeProvider.System).AssignAsync(f.Admin.Id, f.B.Id, f.Staff.Id, new(true, 0), default);
        Assert.Equal(f.B.Id, (await queries.ResolveOriginAsync(copy.Id, f.Staff.Id, default))!.BranchId);
        Assert.Equal("copy_not_found", (await store.UpdateAsync(copy.Id, new(1, "Other branch", null), new(f.Staff.Id, false, f.A.Id), default)).ErrorCode);
        await new BranchStore(f.Mongo, TimeProvider.System).AssignAsync(f.Admin.Id, f.B.Id, f.Staff.Id, new(false, 1), default);
        Assert.Null(await queries.ResolveOriginAsync(copy.Id, f.Staff.Id, default));
    });

    [LocalMongoFact]
    public Task Copy_mutations_require_the_assigned_branch_and_retries_cannot_cross_it() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db); var b = (await CopyPersistenceTests.Seed(db)).Book;
        await f.Mongo.Books.UpdateOneAsync(x => x.Id == b.Id, Builders<Book>.Update.Set(x => x.InventoryBranchId, f.A.Id));
        var catalog = new BranchCatalogStore(f.Mongo, TimeProvider.System);
        await catalog.SetAsync(f.Admin.Id, f.A.Id, b.Id, true, 0, default);
        await catalog.SetAsync(f.Admin.Id, f.B.Id, b.Id, true, 0, default);
        var store = new CopyStore(f.Mongo, TimeProvider.System);
        var request = new CreateCopyRequest("SEDE-A-1", "good", "Estante 1", null, "one");
        Assert.Equal("branch_required", (await store.CreateAsync(b.Id, request, new(f.Staff.Id, false), default)).ErrorCode);
        var copy = (await store.CreateAsync(b.Id, request, new(f.Staff.Id, false, f.A.Id), default)).Copy!;
        Assert.Equal(f.A.Id, copy.BranchId);
        Assert.Equal("branch_forbidden", (await store.UpdateAsync(copy.Id, new(1, "B", null), new(f.Staff.Id, false, f.B.Id), default)).ErrorCode);
        Assert.Equal("copy_not_found", (await store.UpdateAsync(copy.Id, new(1, "B", null), new(f.Admin.Id, true, f.B.Id), default)).ErrorCode);
        await new BranchStore(f.Mongo, TimeProvider.System).AssignAsync(f.Admin.Id, f.B.Id, f.Staff.Id, new(true, 0), default);
        Assert.Equal("idempotency_conflict", (await store.CreateAsync(b.Id, request, new(f.Staff.Id, false, f.B.Id), default)).ErrorCode);
        Assert.Equal(1, (await f.Mongo.Books.Find(x => x.Id == b.Id).SingleAsync()).TotalCopies);
    });
}
