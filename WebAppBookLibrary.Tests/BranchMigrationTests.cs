using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Migrations;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class BranchMigrationTests
{
    [LocalMongoFact]
    public Task Missing_renewal_loan_and_historical_copy_references_block_activation() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await CopyPersistenceTests.Seed(db); var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        await db.GetCollection<RenewalRequest>("RenewalRequests").InsertOneAsync(new() { Id = ObjectId.GenerateNewId().ToString(), BookId = f.Book.Id, UserId = admin.Id, LoanId = ObjectId.GenerateNewId().ToString(), Status = "pending" });
        await db.GetCollection<CopyHistoryEntry>("CopyHistory").InsertOneAsync(new() { Id = ObjectId.GenerateNewId().ToString(), BookId = f.Book.Id, CopyId = ObjectId.GenerateNewId().ToString(), Action = "retired" });
        var migration = new BranchMigration(f.Db, TimeProvider.System);
        var input = new BranchMigrationInput("QA", "INITIAL", "Initial", "UTC");
        var preview = await migration.PreviewAsync(input, default);
        Assert.False(preview.Eligible);
        Assert.Contains(preview.Errors, e => e.StartsWith("orphan_LoanId:RenewalRequests:"));
        Assert.Contains(preview.Errors, e => e.StartsWith("orphan_CopyId:CopyHistory:"));
        Assert.False((await migration.ApplyAsync(input, preview.Fingerprint, admin.Id, default)).Success);
    });

    [LocalMongoFact]
    public Task Interrupted_atomic_application_rolls_back_and_can_be_retried() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await CopyPersistenceTests.Seed(db);
        var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        using var cancel = new CancellationTokenSource();
        var settings = MongoClientSettings.FromConnectionString(Environment.GetEnvironmentVariable("BOOK_LIBRARY_TEST_MONGO_URI"));
        settings.ClusterConfigurator = builder => builder.Subscribe<MongoDB.Driver.Core.Events.CommandStartedEvent>(started => {
            if (started.CommandName == "insert" && started.Command.GetValue("insert", "") == "Branches") cancel.Cancel();
        });
        var interrupted = new BranchMigration(new MongoDBService(new MongoClient(settings).GetDatabase(db.DatabaseNamespace.DatabaseName)), TimeProvider.System);
        var input = new BranchMigrationInput("QA", "INITIAL", "Initial", "UTC");
        var preview = await interrupted.PreviewAsync(input, default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.ApplyAsync(input, preview.Fingerprint, admin.Id, cancel.Token));
        Assert.Equal(0, await db.GetCollection<Organization>("Organizations").CountDocumentsAsync(FilterDefinition<Organization>.Empty));
        Assert.Equal(0, await db.GetCollection<BsonDocument>("BranchState").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var retry = new BranchMigration(f.Db, TimeProvider.System);
        Assert.True((await retry.ApplyAsync(input, (await retry.PreviewAsync(input, default)).Fingerprint, admin.Id, default)).Success);
    });

    [LocalMongoFact]
    public Task Preview_apply_repeat_preserves_ids_counts_and_personal_relations() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await CopyPersistenceTests.Seed(db); var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var copy = (await new WebAppBookLibrary.Services.Copies.CopyStore(f.Db, TimeProvider.System).CreateAsync(f.Book.Id, new("MIG-A", "good", null, null, "one"), f.Actor, default)).Copy!;
        var migration = new BranchMigration(f.Db, TimeProvider.System);
        var input = new BranchMigrationInput("Biblioteca sintética", "INICIAL", "Sede inicial", "America/Mexico_City");
        var preview = await migration.PreviewAsync(input, default);
        Assert.True(preview.Eligible);
        Assert.Equal(0, await db.GetCollection<Branch>("Branches").CountDocumentsAsync(FilterDefinition<Branch>.Empty));
        var applied = await migration.ApplyAsync(input, preview.Fingerprint, admin.Id, default);
        Assert.True(applied.Success);
        var branch = await db.GetCollection<Branch>("Branches").Find(b => true).SingleAsync();
        var current = await f.Db.Books.Find(b => b.Id == f.Book.Id).SingleAsync();
        Assert.Equal(1, current.TotalCopies); Assert.Equal("unit", current.InventoryMode); Assert.Equal(branch.Id, current.InventoryBranchId);
        Assert.Equal(branch.Id, (await db.GetCollection<BookCopy>("BookCopies").Find(c => c.Id == copy.Id).SingleAsync()).BranchId);
        Assert.Equal(copy.InventoryCode, (await db.GetCollection<BookCopy>("BookCopies").Find(c => c.Id == copy.Id).SingleAsync()).InventoryCode);
        var repeat = await migration.PreviewAsync(input, default);
        Assert.True((await migration.ApplyAsync(input, repeat.Fingerprint, admin.Id, default)).Success);
        Assert.Equal(1, await db.GetCollection<Branch>("Branches").CountDocumentsAsync(FilterDefinition<Branch>.Empty));
        Assert.Equal(1, await db.GetCollection<BranchCatalogEntry>("BranchCatalog").CountDocumentsAsync(FilterDefinition<BranchCatalogEntry>.Empty));
    });

    [LocalMongoFact]
    public Task Stale_preview_and_orphans_stop_without_assigning_data() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var f = await CopyPersistenceTests.Seed(db); var admin = await SupportPersistenceTests.UserAsync(f.Db, "admin");
        var migration = new BranchMigration(f.Db, TimeProvider.System);
        var input = new BranchMigrationInput("QA", "INICIAL", "Inicial", "UTC");
        var preview = await migration.PreviewAsync(input, default);
        await f.Db.Books.UpdateOneAsync(b => b.Id == f.Book.Id, Builders<Book>.Update.Set(b => b.Title, "Changed"));
        Assert.Equal("migration_preview_stale", (await migration.ApplyAsync(input, preview.Fingerprint, admin.Id, default)).ErrorCode);
        await db.GetCollection<BookCopy>("BookCopies").InsertOneAsync(new() { Id = ObjectId.GenerateNewId().ToString(), BookId = ObjectId.GenerateNewId().ToString(), InventoryCode = "ORPHAN", Status = "maintenance" });
        Assert.False((await migration.PreviewAsync(input, default)).Eligible);
        Assert.Equal(0, await db.GetCollection<Organization>("Organizations").CountDocumentsAsync(FilterDefinition<Organization>.Empty));
    });
}
