using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Domain.Books;

namespace WebAppBookLibrary.Migrations;

public static class BookSchemaMigration
{
    public static BookMigrationPlan Analyze(BsonDocument document, DateTime nowUtc)
    {
        if (GetInt(document, "SchemaVersion") >= BookRules.CurrentSchemaVersion)
            return new(MigrationDisposition.AlreadyCurrent, document.DeepClone().AsBsonDocument, []);

        var anomalies = new List<string>();
        var title = GetString(document, "Title");
        var author = GetString(document, "Author");
        if (string.IsNullOrWhiteSpace(title)) anomalies.Add("missing_title");
        if (string.IsNullOrWhiteSpace(author)) anomalies.Add("missing_author");
        if (anomalies.Count > 0) return new(MigrationDisposition.Anomalous, null, anomalies);

        var genre = GetString(document, "Genre");
        var available = GetBool(document, "IsAvailable", true);
        var replacement = document.DeepClone().AsBsonDocument;
        replacement["Authors"] = new BsonArray(new[] { author! });
        replacement["Genres"] = new BsonArray(string.IsNullOrWhiteSpace(genre) ? new[] { "Sin clasificar" } : new[] { genre });
        replacement["Description"] = "Sin descripción disponible para este registro heredado.";
        replacement["Language"] = "es";
        replacement["Tags"] = new BsonArray();
        replacement["MediaType"] = MediaTypes.Physical;
        replacement["TotalCopies"] = 1;
        replacement["AvailableCopies"] = available ? 1 : 0;
        replacement["IsActive"] = true;
        replacement["CreatedAt"] = GetDate(document, "CreatedAt") ?? nowUtc;
        replacement["UpdatedAt"] = nowUtc;
        replacement["SchemaVersion"] = BookRules.CurrentSchemaVersion;
        return new(MigrationDisposition.Transformable, replacement, []);
    }

    public static async Task<MigrationReport> RunAsync(IMongoCollection<BsonDocument> collection, bool apply, CancellationToken token)
    {
        long scanned = 0, current = 0, transformable = 0, anomalous = 0, updated = 0;
        var codes = new HashSet<string>(StringComparer.Ordinal);
        using var cursor = await collection.FindAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: token);
        while (await cursor.MoveNextAsync(token))
        {
            foreach (var document in cursor.Current)
            {
                scanned++;
                var plan = Analyze(document, DateTime.UtcNow);
                if (plan.Disposition == MigrationDisposition.AlreadyCurrent) { current++; continue; }
                if (plan.Disposition == MigrationDisposition.Anomalous)
                {
                    anomalous++;
                    foreach (var code in plan.AnomalyCodes) codes.Add(code);
                    continue;
                }
                transformable++;
                if (!apply) continue;
                var result = await collection.ReplaceOneAsync(Builders<BsonDocument>.Filter.Eq("_id", document["_id"]), plan.Replacement!, cancellationToken: token);
                updated += result.ModifiedCount;
            }
        }
        return new(scanned, current, transformable, anomalous, updated, codes.Order().ToArray());
    }

    private static string? GetString(BsonDocument document, string name) => document.TryGetValue(name, out var value) && value.IsString ? value.AsString : null;
    private static int GetInt(BsonDocument document, string name) => document.TryGetValue(name, out var value) && value.IsNumeric ? value.ToInt32() : 0;
    private static bool GetBool(BsonDocument document, string name, bool fallback) => document.TryGetValue(name, out var value) && value.IsBoolean ? value.AsBoolean : fallback;
    private static DateTime? GetDate(BsonDocument document, string name) => document.TryGetValue(name, out var value) && value.IsValidDateTime ? value.ToUniversalTime() : null;
}
