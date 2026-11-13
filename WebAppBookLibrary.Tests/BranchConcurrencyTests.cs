using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services.Branches;

namespace WebAppBookLibrary.Tests;

public sealed class BranchConcurrencyTests
{
    [LocalMongoFact]
    public Task Closure_competes_with_transactional_commitment_without_stranding_it() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await BranchAuthorizationTests.Seed(db);
        var access = new BranchAccessService(f.Mongo);
        using var entered = new SemaphoreSlim(0); using var release = new SemaphoreSlim(0);
        var write = Task.Run(async () => {
            using var session = await db.Client.StartSessionAsync();
            await session.WithTransactionAsync(async (tx, ct) => {
                await access.ValidateForMutationAsync(tx, f.Staff.Id, f.A.Id, ct);
                entered.Release(); await release.WaitAsync(ct);
                await db.GetCollection<BsonDocument>("WaitlistEntries").InsertOneAsync(tx, new() { { "_id", ObjectId.GenerateNewId() }, { "BranchId", f.A.Id }, { "Status", "queued" } }, cancellationToken: ct);
                return true;
            });
        });
        await entered.WaitAsync(TimeSpan.FromSeconds(10));
        var close = new BranchStore(f.Mongo, TimeProvider.System).UpdateAsync(f.Admin.Id, f.A.Id, new("A", "UTC", false, 1), default);
        release.Release(); await write;
        Assert.Equal("branch_has_commitments", (await close).ErrorCode);
        Assert.True((await db.GetCollection<Branch>("Branches").Find(b => b.Id == f.A.Id).SingleAsync()).IsActive);
    });
}
