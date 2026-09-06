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
        var existingAuthors = GetStringArray(document, "Authors");
        var author = GetString(document, "Author");
        if (string.IsNullOrWhiteSpace(title)) anomalies.Add("missing_title");
        if (existingAuthors.Count == 0 && string.IsNullOrWhiteSpace(author)) anomalies.Add("missing_author");
        if (anomalies.Count > 0) return new(MigrationDisposition.Anomalous, null, anomalies);

        var genre = GetString(document, "Genre");
        var available = GetBool(document, "IsAvailable", true);
        var replacement = document.DeepClone().AsBsonDocument;
        if (existingAuthors.Count == 0) replacement["Authors"] = new BsonArray(new[] { author! });
        if (!replacement.Contains("Genres")) replacement["Genres"] = new BsonArray(string.IsNullOrWhiteSpace(genre) ? new[] { "Sin clasificar" } : new[] { genre });
        if (!replacement.Contains("Description")) replacement["Description"] = "Sin descripción disponible para este registro heredado.";
        if (!replacement.Contains("Language")) replacement["Language"] = "es";
        if (!replacement.Contains("Tags")) replacement["Tags"] = new BsonArray();
        if (!replacement.Contains("MediaType")) replacement["MediaType"] = MediaTypes.Physical;
        if (replacement["MediaType"].AsString == MediaTypes.Physical)
        {
            if (!replacement.Contains("TotalCopies")) replacement["TotalCopies"] = 1;
            if (!replacement.Contains("AvailableCopies")) replacement["AvailableCopies"] = available ? 1 : 0;
        }
        else
        {
            replacement.Remove("TotalCopies");
            replacement.Remove("AvailableCopies");
        }
        if (!replacement.Contains("IsActive")) replacement["IsActive"] = true;
        if (!replacement.Contains("CreatedAt")) replacement["CreatedAt"] = nowUtc;
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
                var builder = Builders<BsonDocument>.Filter;
                var schemaGuard = builder.Exists("SchemaVersion", false) | builder.Lt("SchemaVersion", BookRules.CurrentSchemaVersion);
                var result = await collection.ReplaceOneAsync(builder.Eq("_id", document["_id"]) & schemaGuard, plan.Replacement!, cancellationToken: token);
                updated += result.ModifiedCount;
            }
        }
        return new(scanned, current, transformable, anomalous, updated, codes.Order().ToArray());
    }

    private static string? GetString(BsonDocument document, string name) => document.TryGetValue(name, out var value) && value.IsString ? value.AsString : null;
    private static int GetInt(BsonDocument document, string name) => document.TryGetValue(name, out var value) && value.IsNumeric ? value.ToInt32() : 0;
    private static bool GetBool(BsonDocument document, string name, bool fallback) => document.TryGetValue(name, out var value) && value.IsBoolean ? value.AsBoolean : fallback;
    private static DateTime? GetDate(BsonDocument document, string name) => document.TryGetValue(name, out var value) && value.IsValidDateTime ? value.ToUniversalTime() : null;
    private static IReadOnlyList<string> GetStringArray(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsBsonArray
            ? value.AsBsonArray.Where(item => item.IsString && !string.IsNullOrWhiteSpace(item.AsString)).Select(item => item.AsString).ToArray()
            : [];
}
