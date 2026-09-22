using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Migrations;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Tests;

public sealed class CategoryMigrationTests
{
    [Fact]
    public void Unknown_labels_require_explicit_mapping_without_guessing_spelling()
    {
        var books = new[] { Legacy("Ficccion"), Legacy("Ficción") };
        var plan = CategorySchemaMigration.Analyze(books, [], new Dictionary<string, string>(), DateTime.UtcNow);
        Assert.Equal(2, plan.Anomalies.Count);
        Assert.Empty(plan.Categories);
        Assert.All(plan.Anomalies, a => Assert.Equal("mapping_required", a.Code));
    }

    [Fact]
    public void Reviewed_equivalences_share_one_deterministic_category_and_preserve_sources()
    {
        var books = new[] { Legacy("Ficccion"), Legacy(" FICCIÓN ") };
        var snapshot = books.Select(b => b.ToJson()).ToArray();
        var mapping = new Dictionary<string, string> { ["Ficccion"] = "Ficción", ["ficción"] = "Ficción" };
        var first = CategorySchemaMigration.Analyze(books, [], mapping, DateTime.UtcNow);
        var second = CategorySchemaMigration.Analyze(books, [], mapping, DateTime.UtcNow.AddDays(1));
        Assert.Empty(first.Anomalies);
        var category = Assert.Single(first.Categories);
        Assert.Equal(category.Id, Assert.Single(second.Categories).Id);
        Assert.Contains("ficccion", category.Aliases);
        Assert.All(first.Books, b => Assert.Equal(category.Id, Assert.Single(b.CategoryIds)));
        Assert.Equal(snapshot, books.Select(b => b.ToJson()));
    }

    [Fact]
    public void Existing_ids_are_preserved_and_missing_references_block_apply()
    {
        var category = new Category { Id = ObjectId.GenerateNewId().ToString(), Name = "Terror", NormalizedName = "terror", Slug = "terror" };
        var valid = Legacy("Nombre antiguo"); valid["CategoryIds"] = new BsonArray(new[] { category.Id });
        var invalid = Legacy("Terror"); invalid["CategoryIds"] = new BsonArray(new[] { ObjectId.GenerateNewId().ToString() });
        var plan = CategorySchemaMigration.Analyze([valid, invalid], [category], new Dictionary<string, string>(), DateTime.UtcNow);
        Assert.Equal(1, plan.AlreadyCurrent);
        Assert.Contains(plan.Anomalies, a => a.Code == "invalid_category_reference");
        Assert.Empty(plan.Books);
    }

    [LocalMongoFact]
    public Task Dry_run_apply_and_repeat_preserve_legacy_metadata() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var books = db.GetCollection<BsonDocument>("Books");
        var source = Legacy("Terror"); source["Notes"] = "Preserve unrelated metadata";
        await books.InsertOneAsync(source);
        var mapping = new Dictionary<string, string> { ["Terror"] = "Terror" };
        var dry = await CategorySchemaMigration.RunAsync(db, mapping, false, default);
        Assert.Empty(dry.Anomalies); Assert.Equal(0, dry.Updated);
        Assert.Equal(0, await db.GetCollection<Category>("Categories").CountDocumentsAsync(FilterDefinition<Category>.Empty));
        var applied = await CategorySchemaMigration.RunAsync(db, mapping, true, default);
        Assert.Equal(1, applied.Updated);
        Assert.Equal(0, (await CategorySchemaMigration.RunAsync(db, mapping, true, default)).Updated);
        var saved = await books.Find(new BsonDocument("_id", source["_id"])).SingleAsync();
        Assert.Equal(source["Genres"], saved["Genres"]);
        Assert.Equal(source["Notes"], saved["Notes"]);
        Assert.Single(saved["CategoryIds"].AsBsonArray);
    });

    [LocalMongoFact]
    public Task Ambiguous_migration_never_partially_writes_valid_books() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var books = db.GetCollection<BsonDocument>("Books");
        await books.InsertManyAsync([Legacy("Terror"), Legacy("Unknown")]);
        var report = await CategorySchemaMigration.RunAsync(db, new Dictionary<string, string> { ["Terror"] = "Terror" }, true, default);
        Assert.Contains(report.Anomalies, a => a.Code == "mapping_required");
        Assert.Equal(0, report.Updated);
        Assert.Equal(0, await db.GetCollection<Category>("Categories").CountDocumentsAsync(FilterDefinition<Category>.Empty));
        Assert.Equal(0, await books.CountDocumentsAsync(Builders<BsonDocument>.Filter.Exists("CategoryIds")));
    });

    [Fact]
    public void Planned_alias_collision_is_reported_before_apply()
    {
        var plan = CategorySchemaMigration.Analyze([Legacy("A"), Legacy("B")], [],
            new Dictionary<string, string> { ["A"] = "B", ["B"] = "C" }, DateTime.UtcNow);
        Assert.Contains(plan.Anomalies, a => a.Code == "ambiguous_planned_alias");
    }

    private static BsonDocument Legacy(string genre) => new() { ["_id"] = ObjectId.GenerateNewId(), ["Title"] = "Migration test", ["Genres"] = new BsonArray(new[] { genre }) };
}
