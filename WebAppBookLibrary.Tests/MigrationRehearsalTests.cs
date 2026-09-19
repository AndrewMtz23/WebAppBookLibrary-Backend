using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using WebAppBookLibrary.Migrations;

namespace WebAppBookLibrary.Tests;

public sealed class MigrationRehearsalTests
{
    [LocalMongoFact]
    public Task Snapshot_dry_run_apply_idempotence_and_restore_preserve_exact_local_documents() => StaffReviewRegressionTests.WithDatabase(async database =>
    {
        var books = database.GetCollection<BsonDocument>("Books");
        var source = new[] {
            new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["Title"] = "Ejemplar heredado", ["Author"] = "Autor ficticio", ["Genre"] = "Ensayo", ["IsAvailable"] = false },
            new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["Title"] = "Documento anómalo" }
        };
        await books.InsertManyAsync(source);
        // BSON snapshot retains types and original identifiers, unlike a lossy JSON export.
        var snapshot = (await books.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync()).Select(document => document.ToBson()).ToArray();
        var dry = await BookSchemaMigration.RunAsync(books, false, default);
        Assert.Equal(2, dry.Scanned); Assert.Equal(1, dry.Transformable); Assert.Equal(1, dry.Anomalous); Assert.Equal(0, dry.Updated);
        Assert.Equal(source[0], await books.Find(new BsonDocument("_id", source[0]["_id"])).SingleAsync());
        var applied = await BookSchemaMigration.RunAsync(books, true, default);
        Assert.Equal(1, applied.Updated);
        var migrated = await books.Find(new BsonDocument("_id", source[0]["_id"])).SingleAsync();
        Assert.Equal(1, migrated["TotalCopies"].AsInt32); Assert.Equal(0, migrated["AvailableCopies"].AsInt32);
        Assert.Equal(source[1], await books.Find(new BsonDocument("_id", source[1]["_id"])).SingleAsync());
        Assert.Equal(0, (await BookSchemaMigration.RunAsync(books, true, default)).Updated);
        // Restoration targets only this randomly named local test database.
        await books.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        await books.InsertManyAsync(snapshot.Select(bytes => BsonSerializer.Deserialize<BsonDocument>(bytes)));
        var restored = await books.Find(FilterDefinition<BsonDocument>.Empty).Sort(Builders<BsonDocument>.Sort.Ascending("_id")).ToListAsync();
        Assert.Equal(snapshot.Length, restored.Count);
        for (var i = 0; i < snapshot.Length; i++) Assert.Equal(snapshot[i], restored[i].ToBson());
    });
}
