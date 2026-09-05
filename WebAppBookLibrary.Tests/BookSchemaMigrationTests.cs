using MongoDB.Bson;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Migrations;

namespace WebAppBookLibrary.Tests;

public sealed class BookSchemaMigrationTests
{
    [Fact]
    public void Analyze_TransformsLegacyBookDeterministically()
    {
        var legacy = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(), ["Title"] = "Legacy", ["Author"] = "Author",
            ["Genre"] = "Novel", ["Year"] = 1999, ["IsAvailable"] = false
        };
        var now = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);

        var first = BookSchemaMigration.Analyze(legacy, now);
        var second = BookSchemaMigration.Analyze(first.Replacement!, now);

        Assert.Equal(MigrationDisposition.Transformable, first.Disposition);
        Assert.Equal(["Author"], first.Replacement!["Authors"].AsBsonArray.Select(value => value.AsString));
        Assert.Equal(["Novel"], first.Replacement["Genres"].AsBsonArray.Select(value => value.AsString));
        Assert.Equal(MediaTypes.Physical, first.Replacement["MediaType"].AsString);
        Assert.Equal(1, first.Replacement["TotalCopies"].AsInt32);
        Assert.Equal(0, first.Replacement["AvailableCopies"].AsInt32);
        Assert.Equal(BookRules.CurrentSchemaVersion, first.Replacement["SchemaVersion"].AsInt32);
        Assert.Equal(MigrationDisposition.AlreadyCurrent, second.Disposition);
    }

    [Fact]
    public void Analyze_LeavesAnomalousDocumentUntouched()
    {
        var invalid = new BsonDocument { ["_id"] = ObjectId.GenerateNewId(), ["Title"] = "Missing author" };

        var result = BookSchemaMigration.Analyze(invalid, DateTime.UtcNow);

        Assert.Equal(MigrationDisposition.Anomalous, result.Disposition);
        Assert.Null(result.Replacement);
        Assert.Contains("missing_author", result.AnomalyCodes);
    }
}
