using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Domain.Categories;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Migrations;

public sealed record CategoryMigrationAnomaly(string BookId, string Code, string? Label = null);
public sealed record CategoryBookMigration(BsonDocument Source, IReadOnlyList<string> CategoryIds);
public sealed record CategoryMigrationPlan(IReadOnlyList<Category> Categories, IReadOnlyList<CategoryBookMigration> Books,
    int AlreadyCurrent, IReadOnlyList<CategoryMigrationAnomaly> Anomalies);
public sealed record CategoryMigrationReport(int Scanned, int AlreadyCurrent, int Transformable, int Updated,
    IReadOnlyList<CategoryMigrationAnomaly> Anomalies, IReadOnlyList<object> Equivalences);

/// <summary>Explicitly reviewed vocabulary migration. No environment loading and no implicit spelling corrections.</summary>
public static class CategorySchemaMigration
{
    public static CategoryMigrationPlan Analyze(IReadOnlyList<BsonDocument> books, IReadOnlyList<Category> existing,
        IReadOnlyDictionary<string, string> mapping, DateTime nowUtc)
    {
        var anomalies = new List<CategoryMigrationAnomaly>();
        var planned = new Dictionary<string, Category>(StringComparer.Ordinal);
        var writes = new List<CategoryBookMigration>();
        var names = existing.GroupBy(c => c.NormalizedName).ToDictionary(g => g.Key, g => g.First());
        foreach (var duplicate in existing.GroupBy(c => c.NormalizedName).Where(g => g.Count() > 1))
            anomalies.Add(new("", "duplicate_existing_name", duplicate.Key));
        foreach (var duplicate in existing.GroupBy(c => c.Slug).Where(g => g.Count() > 1))
            anomalies.Add(new("", "duplicate_existing_slug", duplicate.Key));
        var aliases = new Dictionary<string, Category>(StringComparer.Ordinal);
        foreach (var category in existing)
            foreach (var alias in category.Aliases.Append(category.NormalizedName))
                if (aliases.TryGetValue(alias, out var owner) && owner.Id != category.Id)
                    anomalies.Add(new("", "ambiguous_existing_alias", alias));
                else aliases[alias] = category;
        var reviewed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in mapping)
        {
            if (pair.Value is null) { anomalies.Add(new("", "invalid_mapping", pair.Key)); continue; }
            var key = CategoryRules.NormalizeName(pair.Key);
            var value = CategoryRules.DisplayName(pair.Value);
            if (key.Length == 0 || value.Length is < 1 or > 80 || value.Any(char.IsControl))
            { anomalies.Add(new("", "invalid_mapping", pair.Key)); continue; }
            if (reviewed.TryGetValue(key, out var previous) && CategoryRules.NormalizeName(previous) != CategoryRules.NormalizeName(value))
                anomalies.Add(new("", "ambiguous_mapping", pair.Key));
            else reviewed[key] = value;
        }
        var current = 0;
        foreach (var book in books)
        {
            var id = book.GetValue("_id", BsonNull.Value).ToString() ?? "";
            if (!ObjectId.TryParse(id, out _)) { anomalies.Add(new(id, "invalid_book_id")); continue; }
            if (book.TryGetValue("CategoryIds", out var ids) && (!ids.IsBsonArray || ids.AsBsonArray.Count > 0))
            {
                if (!ids.IsBsonArray || ids.AsBsonArray.Count > 8 || ids.AsBsonArray.Distinct().Count() != ids.AsBsonArray.Count ||
                    ids.AsBsonArray.Any(v => !v.IsString || !existing.Any(c => c.Id == v.AsString)))
                    anomalies.Add(new(id, "invalid_category_reference"));
                else current++;
                continue;
            }
            var labels = new List<string>();
            if (book.TryGetValue("Genres", out var rawGenres) && !rawGenres.IsBsonArray)
            { anomalies.Add(new(id, "invalid_legacy_genres")); continue; }
            if (book.TryGetValue("Genres", out var genres) && genres.IsBsonArray && genres.AsBsonArray.Count > 0)
            {
                if (genres.AsBsonArray.Any(v => !v.IsString || string.IsNullOrWhiteSpace(v.AsString)))
                { anomalies.Add(new(id, "invalid_legacy_genres")); continue; }
                labels.AddRange(genres.AsBsonArray.Select(v => v.AsString));
            }
            else if (book.TryGetValue("Genre", out var genre) && genre.IsString && !string.IsNullOrWhiteSpace(genre.AsString)) labels.Add(genre.AsString);
            if (labels.Count == 0) { anomalies.Add(new(id, "missing_genres")); continue; }
            var assigned = new List<string>();
            var startErrors = anomalies.Count;
            foreach (var label in labels)
            {
                var normalized = CategoryRules.NormalizeName(label);
                var hasMapping = reviewed.TryGetValue(normalized, out var display);
                aliases.TryGetValue(normalized, out var matched);
                if (!hasMapping && matched is null) { anomalies.Add(new(id, "mapping_required", label)); continue; }
                display ??= matched!.Name;
                var canonical = CategoryRules.NormalizeName(display);
                if (matched is not null && matched.NormalizedName != canonical)
                { anomalies.Add(new(id, "alias_owned_by_other_category", label)); continue; }
                if (!planned.TryGetValue(canonical, out var category))
                {
                    if (names.TryGetValue(canonical, out var known))
                        category = new Category { Id = known.Id, Name = known.Name, NormalizedName = known.NormalizedName, Slug = known.Slug,
                            Description = known.Description, IsActive = known.IsActive, Version = known.Version, ReferenceVersion = known.ReferenceVersion,
                            CreatedAt = known.CreatedAt, UpdatedAt = known.UpdatedAt, Aliases = [.. known.Aliases] };
                    else
                    {
                        var categoryId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("booklibrary:category:" + canonical)))[..24].ToLowerInvariant();
                        var slug = CategoryRules.CreateSlug(display);
                        if (existing.Concat(planned.Values).Any(c => c.Slug == slug && c.NormalizedName != canonical))
                            slug = CategoryRules.CreateSlug(display, categoryId[..8]);
                        category = new Category { Id = categoryId, Name = display, NormalizedName = canonical, Slug = slug, CreatedAt = nowUtc, UpdatedAt = nowUtc };
                    }
                    planned[canonical] = category;
                }
                category.Aliases = category.Aliases.Concat(new[] { normalized, canonical }).Distinct(StringComparer.Ordinal).ToList();
                assigned.Add(category.Id);
            }
            assigned = assigned.Distinct().ToList();
            if (assigned.Count > 8) anomalies.Add(new(id, "too_many_categories"));
            if (startErrors == anomalies.Count) writes.Add(new(book.DeepClone().AsBsonDocument, assigned));
        }
        var finalCategories = existing.Where(c => !planned.Values.Any(p => p.Id == c.Id)).Concat(planned.Values).ToArray();
        foreach (var duplicate in finalCategories.SelectMany(c => c.Aliases.Append(c.NormalizedName).Distinct().Select(alias => new { alias, c.Id }))
            .GroupBy(value => value.alias).Where(g => g.Select(value => value.Id).Distinct().Count() > 1))
            anomalies.Add(new("", "ambiguous_planned_alias", duplicate.Key));
        return new(planned.Values.ToArray(), writes, current, anomalies);
    }

    public static async Task<CategoryMigrationReport> RunAsync(IMongoDatabase database, IReadOnlyDictionary<string, string> mapping, bool apply, CancellationToken token)
    {
        var books = database.GetCollection<BsonDocument>("Books");
        var categories = database.GetCollection<Category>("Categories");
        // Explicit bounded first release; never silently truncate a migration.
        var source = await books.Find(FilterDefinition<BsonDocument>.Empty).Limit(10001).ToListAsync(token);
        if (source.Count > 10000) return new(source.Count, 0, 0, 0, [new("", "dataset_exceeds_10000_use_reviewed_batches")], []);
        var existing = await categories.Find(FilterDefinition<Category>.Empty).ToListAsync(token);
        var plan = Analyze(source, existing, mapping, DateTime.UtcNow);
        var anomalies = plan.Anomalies.ToList();
        var updated = 0;
        if (apply && anomalies.Count == 0)
        {
            await categories.Indexes.CreateManyAsync([
                new CreateIndexModel<Category>(Builders<Category>.IndexKeys.Ascending(c => c.NormalizedName), new CreateIndexOptions { Name = "ux_categories_name", Unique = true }),
                new CreateIndexModel<Category>(Builders<Category>.IndexKeys.Ascending(c => c.Slug), new CreateIndexOptions { Name = "ux_categories_slug", Unique = true }),
                new CreateIndexModel<Category>(Builders<Category>.IndexKeys.Ascending(c => c.Aliases), new CreateIndexOptions<Category> { Name = "ux_categories_aliases", Unique = true, PartialFilterExpression = Builders<Category>.Filter.Exists("Aliases.0") })
            ], cancellationToken: token);
            foreach (var category in plan.Categories)
            {
                if (existing.Any(c => c.Id == category.Id))
                {
                    var changed = await categories.UpdateOneAsync(c => c.Id == category.Id && c.Version == category.Version,
                        Builders<Category>.Update.AddToSetEach(c => c.Aliases, category.Aliases), cancellationToken: token);
                    if (changed.MatchedCount == 0) { anomalies.Add(new("", "category_changed_rerun_dry_run", category.Name)); break; }
                }
                else await categories.InsertOneAsync(category, cancellationToken: token);
            }
            if (anomalies.Count == 0)
                foreach (var book in plan.Books)
                {
                    using var session = await database.Client.StartSessionAsync(cancellationToken: token);
                    var changed = await session.WithTransactionAsync(async (transaction, ct) =>
                    {
                        foreach (var id in book.CategoryIds)
                        {
                            var category = plan.Categories.Single(c => c.Id == id);
                            var locked = await categories.UpdateOneAsync(transaction, c => c.Id == id && c.Version == category.Version,
                                Builders<Category>.Update.Inc(c => c.ReferenceVersion, 1), cancellationToken: ct);
                            if (locked.MatchedCount == 0) throw new InvalidOperationException("Category changed during migration; rerun dry-run.");
                        }
                        var f = Builders<BsonDocument>.Filter;
                        FilterDefinition<BsonDocument> guard = f.Eq("_id", book.Source["_id"]);
                        foreach (var field in new[] { "CategoryIds", "Genres", "Genre", "UpdatedAt" })
                            guard &= book.Source.TryGetValue(field, out var value) ? f.Eq(field, value) : f.Exists(field, false);
                        var result = await books.UpdateOneAsync(transaction, guard,
                            Builders<BsonDocument>.Update.Set("CategoryIds", new BsonArray(book.CategoryIds)).Set("UpdatedAt", DateTime.UtcNow), cancellationToken: ct);
                        return result.ModifiedCount == 1;
                    }, cancellationToken: token);
                    if (changed) updated++; else anomalies.Add(new(book.Source["_id"].ToString()!, "book_changed_rerun_dry_run"));
                }
        }
        return new(source.Count, plan.AlreadyCurrent, plan.Books.Count, updated, anomalies,
            plan.Categories.Select(c => (object)new { c.Id, c.Name, c.Slug, c.Aliases }).ToArray());
    }
}
