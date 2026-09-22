using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Domain.Categories;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Contracts.Categories;
using WebAppBookLibrary.Contracts.Books;
namespace WebAppBookLibrary.Services;

public sealed class CategoryService(MongoDBService database)
{
    private IMongoCollection<Category> Categories => database.Categories;
    public static CategoryResponse ToResponse(Category c, long? count = null) => new(c.Id, c.Name, c.Slug, c.Description, c.IsActive, c.Version, c.CreatedAt, c.UpdatedAt, count);
    public async Task<PagedResult<CategoryResponse>> SearchAsync(CategoryQuery query, bool admin, CancellationToken ct)
    {
        var f = Builders<Category>.Filter;
        var filter = admin ? f.Empty : f.Eq(c => c.IsActive, true);
        if (admin && query.IsActive.HasValue) filter &= f.Eq(c => c.IsActive, query.IsActive.Value);
        if (!string.IsNullOrWhiteSpace(query.Query)) filter &= f.Regex(c => c.NormalizedName, new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(CategoryRules.NormalizeName(query.Query))));
        var page = Math.Max(1, query.Page); var size = Math.Clamp(query.PageSize, 1, 100);
        var total = await Categories.CountDocumentsAsync(filter, cancellationToken: ct);
        var offset = ((long)page - 1) * size;
        if (offset >= total) return new([], page, size, total);
        var rows = await Categories.Aggregate().Match(filter).SortBy(c => c.NormalizedName).ThenBy(c => c.Id).Skip(offset).Limit(size).ToListAsync(ct);
        var counts = new Dictionary<string, long>();
        if (admin)
        {
            // Batch read: includes inactive and legacy books when reporting usage.
            var books = await database.Books.Find(ReferenceFilter(rows)).Project(b => new Book { CategoryIds = b.CategoryIds, Genres = b.Genres, Genre = b.Genre }).ToListAsync(ct);
            foreach (var c in rows) counts[c.Id] = books.LongCount(b => References(b, c));
        }
        return new(rows.Select(c => ToResponse(c, admin ? counts.GetValueOrDefault(c.Id) : null)).ToArray(), page, size, total);
    }
    public async Task<CategoryMutationResult> CreateAsync(CategoryWriteRequest request, CancellationToken ct)
    {
        var name = CategoryRules.DisplayName(request.Name); var normalized = CategoryRules.NormalizeName(name);
        var c = new Category { Id = ObjectId.GenerateNewId().ToString(), Name = name, NormalizedName = normalized, Slug = CategoryRules.CreateSlug(name), Description = request.Description?.Trim(), Aliases = [normalized] };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try { await Categories.InsertOneAsync(c, cancellationToken: ct); return new(true, "", ToResponse(c, 0)); }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                if (await Categories.Find(x => x.NormalizedName == normalized || x.Aliases.Contains(normalized)).AnyAsync(ct)) return new(false, "category_duplicate");
                c.Slug = CategoryRules.CreateSlug(name, c.Id);
            }
        }
        return new(false, "category_duplicate");
    }
    public Task<CategoryMutationResult> UpdateAsync(string id, CategoryWriteRequest request, CancellationToken ct) => MutateAsync(id, request.Version,
        Builders<Category>.Update.Set(c => c.Name, CategoryRules.DisplayName(request.Name)).Set(c => c.NormalizedName, CategoryRules.NormalizeName(request.Name))
            .Set(c => c.Description, request.Description?.Trim()).AddToSet(c => c.Aliases, CategoryRules.NormalizeName(request.Name)), ct);
    public Task<CategoryMutationResult> StatusAsync(string id, CategoryStatusRequest request, CancellationToken ct) => MutateAsync(id, request.Version, Builders<Category>.Update.Set(c => c.IsActive, request.IsActive), ct);
    private async Task<CategoryMutationResult> MutateAsync(string id, long version, UpdateDefinition<Category> update, CancellationToken ct)
    {
        if (version < 1) return new(false, "category_version_required");
        try
        {
            var c = await Categories.FindOneAndUpdateAsync(x => x.Id == id && x.Version == version,
                update.Inc(x => x.Version, 1).Inc(x => x.ReferenceVersion, 1).Set(x => x.UpdatedAt, DateTime.UtcNow),
                new FindOneAndUpdateOptions<Category, Category> { ReturnDocument = ReturnDocument.After }, ct);
            return c is null ? new(false, await Categories.Find(x => x.Id == id).AnyAsync(ct) ? "category_version_conflict" : "category_not_found") : new(true, "", ToResponse(c));
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return new(false, "category_duplicate"); }
        catch (MongoCommandException ex) when (ex.Code == 11000) { return new(false, "category_duplicate"); }
    }
    public async Task<CategoryMutationResult> DeleteAsync(string id, long version, CancellationToken ct)
    {
        if (version < 1) return new(false, "category_version_required");
        using var session = await database._database.Client.StartSessionAsync(cancellationToken: ct);
        return await session.WithTransactionAsync(async (transaction, token) =>
        {
            var c = await Categories.FindOneAndUpdateAsync(transaction, Builders<Category>.Filter.Eq(x => x.Id, id) & Builders<Category>.Filter.Eq(x => x.Version, version), Builders<Category>.Update.Inc(x => x.ReferenceVersion, 1), new FindOneAndUpdateOptions<Category, Category> { ReturnDocument = ReturnDocument.After }, token);
            if (c is null) return new CategoryMutationResult(false, await Categories.Find(transaction, x => x.Id == id).AnyAsync(token) ? "category_version_conflict" : "category_not_found");
            // Legacy names are normalized in memory; regex cannot safely implement Unicode normalization.
            var books = await database.Books.Find(transaction, ReferenceFilter([c])).Project(b => new Book { CategoryIds = b.CategoryIds, Genres = b.Genres, Genre = b.Genre }).ToListAsync(token);
            if (books.Any(b => References(b, c))) return new CategoryMutationResult(false, "category_in_use");
            await Categories.DeleteOneAsync(transaction, x => x.Id == id, cancellationToken: token);
            return new CategoryMutationResult(true, "");
        }, cancellationToken: ct);
    }
    public async Task EnrichAsync(IEnumerable<Book> source, CancellationToken ct)
    {
        var books = source.ToArray(); var ids = books.SelectMany(b => b.CategoryIds).Distinct().ToArray();
        if (ids.Length == 0) return;
        var categories = (await Categories.Find(c => ids.Contains(c.Id)).ToListAsync(ct)).ToDictionary(c => c.Id);
        foreach (var b in books) b.Categories = b.CategoryIds.Where(categories.ContainsKey).Select(id => categories[id]).Select(c => new BookCategoryResponse(c.Id, c.Name, c.Slug, c.IsActive)).ToList();
    }
    public async Task ResolveWriteAsync(Book book, BookWriteRequest request, Book? previous, CancellationToken ct)
    {
        var names = request.Genres.Count > 0 ? request.Genres : string.IsNullOrWhiteSpace(request.Genre) ? [] : new[] { request.Genre };
        var normalized = names.Select(CategoryRules.NormalizeName).Distinct().ToArray();
        List<Category> resolved;
        if (request.CategoryIds is not null)
        {
            if (request.CategoryIds.Count is < 1 or > 8 || request.CategoryIds.Any(id => !ObjectId.TryParse(id, out _)) || request.CategoryIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.CategoryIds.Count) throw new CategoryReferenceException("category_ids_invalid");
            var ids = request.CategoryIds.Select(id => ObjectId.Parse(id).ToString()).ToArray();
            resolved = await Categories.Find(c => ids.Contains(c.Id)).ToListAsync(ct);
            if (resolved.Count != ids.Length) throw new CategoryReferenceException("category_not_found");
            resolved = ids.Select(id => resolved.Single(c => c.Id == id)).ToList();
            if (normalized.Length > 0)
            {
                var legacy = await ResolveNamesAsync(normalized, ct);
                if (!legacy.Select(c => c.Id).ToHashSet().SetEquals(ids)) throw new CategoryReferenceException("category_contract_conflict");
            }
        }
        else resolved = await ResolveNamesAsync(normalized, ct);
        if (resolved.Count is < 1 or > 8) throw new CategoryReferenceException("category_ids_invalid");
        if (resolved.Any(c => !c.IsActive && !(previous?.CategoryIds.Contains(c.Id) ?? false))) throw new CategoryReferenceException("category_inactive");
        book.CategoryIds = resolved.Select(c => c.Id).ToList();
        book.Categories = resolved.Select(c => new BookCategoryResponse(c.Id, c.Name, c.Slug, c.IsActive)).ToList();
        book.Genres = resolved.Select(c => c.Name).ToList(); book.Genre = book.Genres.FirstOrDefault() ?? "";
    }
    private async Task<List<Category>> ResolveNamesAsync(string[] names, CancellationToken ct)
    {
        var rows = await Categories.Find(Builders<Category>.Filter.In(c => c.NormalizedName, names) | Builders<Category>.Filter.AnyIn(c => c.Aliases, names)).ToListAsync(ct);
        if (names.Any(n => rows.Count(c => c.NormalizedName == n || c.Aliases.Contains(n)) != 1)) throw new CategoryReferenceException("category_unknown_genre");
        return rows;
    }
    public async Task<NormalizedBookQuery> ResolveQueryAsync(NormalizedBookQuery query, CancellationToken ct)
    {
        if (query.CategoryId is not null && !ObjectId.TryParse(query.CategoryId, out _)) throw new CategoryReferenceException("category_ids_invalid");
        Category? category = null;
        if (query.Genre is not null)
        {
            var name = CategoryRules.NormalizeName(query.Genre);
            category = await Categories.Find(c => c.NormalizedName == name || c.Aliases.Contains(name) || c.Slug == query.Genre).FirstOrDefaultAsync(ct);
            if (query.CategoryId is not null && category?.Id != ObjectId.Parse(query.CategoryId).ToString()) throw new CategoryReferenceException("category_contract_conflict");
        }
        if (query.CategoryId is not null) category = await Categories.Find(c => c.Id == ObjectId.Parse(query.CategoryId).ToString()).FirstOrDefaultAsync(ct);
        var aliases = category is null ? query.Genre is null ? [] : new[] { CategoryRules.NormalizeName(query.Genre) } : category.Aliases.Append(category.NormalizedName).Distinct().ToArray();
        var legacyIds = new List<string>();
        if (aliases.Length > 0)
        {
            var legacy = await database.Books.Find(LegacyFilter()).Project(b => new Book { Id = b.Id, Genres = b.Genres, Genre = b.Genre }).ToListAsync(ct);
            legacyIds = legacy.Where(b => LegacyNames(b).Any(aliases.Contains)).Select(b => b.Id).ToList();
        }
        return query with { CategoryId = category?.Id ?? query.CategoryId, LegacyCategoryBookIds = legacyIds, Genre = null, CategoryFilter = query.CategoryId is not null || query.Genre is not null };
    }
    public static IEnumerable<string> LegacyNames(Book b) => b.Genres.Append(b.Genre).Where(n => !string.IsNullOrWhiteSpace(n)).Select(CategoryRules.NormalizeName).Distinct();
    public static bool References(Book b, Category c) => b.CategoryIds.Contains(c.Id) || b.CategoryIds.Count == 0 && LegacyNames(b).Any(n => n == c.NormalizedName || c.Aliases.Contains(n));
    private static FilterDefinition<Book> LegacyFilter() => Builders<Book>.Filter.Size(b => b.CategoryIds, 0) | Builders<Book>.Filter.Exists(b => b.CategoryIds, false);
    private static FilterDefinition<Book> ReferenceFilter(IEnumerable<Category> categories) => Builders<Book>.Filter.AnyIn(b => b.CategoryIds, categories.Select(c => c.Id)) | LegacyFilter();
}
public sealed record CategoryMutationResult(bool Success, string ErrorCode, CategoryResponse? Category = null);
