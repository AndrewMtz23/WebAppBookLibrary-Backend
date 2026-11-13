using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Branches;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Tests;

public sealed class BranchAuthorizationTests
{
    internal static async Task<(MongoDBService Mongo, User Admin, User Staff, Branch A, Branch B)> Seed(IMongoDatabase db)
    {
        var mongo = new MongoDBService(db);
        var admin = await SupportPersistenceTests.UserAsync(mongo, "admin");
        var staff = await SupportPersistenceTests.UserAsync(mongo, "librarian");
        await db.GetCollection<Organization>("Organizations").InsertOneAsync(new() { Id = "initial", Name = "Synthetic library" });
        await BranchIndexes.CreateAsync(db);
        var store = new BranchStore(mongo, TimeProvider.System);
        var a = (await store.CreateAsync(admin.Id, new("A", "Sede A", "America/Mexico_City"), default)).Value!;
        var b = (await store.CreateAsync(admin.Id, new("B", "Sede B", "America/Mexico_City"), default)).Value!;
        Assert.True((await store.AssignAsync(admin.Id, a.Id, staff.Id, new(true, 0), default)).Success);
        return (mongo, admin, staff, a, b);
    }

    [LocalMongoFact]
    public Task Role_does_not_grant_other_branches_and_revocation_is_immediate() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await Seed(db); var access = new BranchAccessService(f.Mongo);
        Assert.NotNull(await access.ResolveAsync(f.Staff.Id, f.A.Id, default));
        Assert.Null(await access.ResolveAsync(f.Staff.Id, f.B.Id, default));
        Assert.NotNull(await access.ResolveAsync(f.Admin.Id, f.B.Id, default));
        var reader = await SupportPersistenceTests.UserAsync(f.Mongo, "user");
        Assert.Null(await access.ResolveAsync(reader.Id, f.A.Id, default));
        var store = new BranchStore(f.Mongo, TimeProvider.System);
        Assert.True((await store.AssignAsync(f.Admin.Id, f.B.Id, f.Staff.Id, new(true, 0), default)).Success);
        Assert.Equal(2, (await store.MineAsync(f.Staff.Id, default)).Count);
        Assert.True((await store.AssignAsync(f.Admin.Id, f.A.Id, f.Staff.Id, new(false, 1), default)).Success);
        Assert.Null(await access.ResolveAsync(f.Staff.Id, f.A.Id, default));
        Assert.Equal("branch_forbidden", (await store.CreateAsync(f.Staff.Id, new("C", "C", "UTC"), default)).ErrorCode);
    });

    [LocalMongoFact]
    public Task Closing_preserves_history_rejects_commitments_and_uses_versions() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await Seed(db); var store = new BranchStore(f.Mongo, TimeProvider.System);
        var loans = db.GetCollection<BsonDocument>("Loans");
        var loan = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "BranchId", f.A.Id }, { "Status", "overdue" }, { "IsReturned", false } };
        await loans.InsertOneAsync(loan);
        Assert.Equal("branch_has_commitments", (await store.UpdateAsync(f.Admin.Id, f.A.Id, new("Sede A", "UTC", false, 1), default)).ErrorCode);
        await loans.UpdateOneAsync(new BsonDocument("_id", loan["_id"]), new BsonDocument("$set", new BsonDocument { { "Status", "returned" }, { "IsReturned", true } }));
        Assert.True((await store.UpdateAsync(f.Admin.Id, f.A.Id, new("Sede A", "UTC", false, 1), default)).Success);
        Assert.Null(await new BranchAccessService(f.Mongo).ResolveAsync(f.Staff.Id, f.A.Id, default));
        Assert.Equal("version_conflict", (await store.UpdateAsync(f.Admin.Id, f.A.Id, new("Old", "UTC", true, 1), default)).ErrorCode);
        Assert.Equal(1, await loans.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.NotEmpty(await f.Mongo.LogEntries.Find(l => l.TargetId == f.A.Id).ToListAsync());
    });
}
