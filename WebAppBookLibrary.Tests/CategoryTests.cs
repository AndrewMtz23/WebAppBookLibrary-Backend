using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Categories;
using WebAppBookLibrary.Controllers;
using WebAppBookLibrary.Domain.Categories;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;
namespace WebAppBookLibrary.Tests;
public sealed class CategoryTests
{
    [Theory]
    [InlineData("  FICCI\u00d3N   hist\u00f3rica ", "ficcion historica")]
    [InlineData("NIN\u0303OS", "ni\u00f1os")]
    [InlineData("Ni\u00f1os", "ni\u00f1os")]
    [InlineData("Ninos", "ninos")]
    [InlineData("Fantas\u00eda \U0001f4da", "fantasia \U0001f4da")]
    public void Normalization_is_deterministic_and_preserves_enye(string input, string expected) => Assert.Equal(expected, CategoryRules.NormalizeName(input));
    [Fact]
    public void Category_and_id_boundaries_are_validated()
    {
        Assert.NotEmpty(new CategoryWriteRequest { Name = " ", Description = new string('x', 501) }.Validate(new ValidationContext(new object())));
        Assert.NotEmpty(new CategoryWriteRequest { Name = new string('x', 81) }.Validate(new ValidationContext(new object())));
        Assert.Empty(new CategoryWriteRequest { Name = new string('x', 80), Description = new string('x', 500) }.Validate(new ValidationContext(new object())));
        var id = ObjectId.GenerateNewId().ToString();
        foreach (var ids in new IReadOnlyList<string>[] { [], [id, id.ToUpperInvariant()], ["bad"], Enumerable.Range(0,9).Select(_ => ObjectId.GenerateNewId().ToString()).ToArray() })
            Assert.Contains(Request(ids).Validate(new ValidationContext(new object())), e => e.MemberNames.Contains("CategoryIds"));
        Assert.DoesNotContain(Request([id]).Validate(new ValidationContext(new object())), e => e.MemberNames.Contains("Genres"));
    }
    [Theory]
    [InlineData(RoleNames.User)]
    [InlineData(RoleNames.Librarian)]
    public async Task Non_admin_cannot_mutate_categories(string role)
    {
        await using var app = await StaffHttpQueryTests.StartApp(s => s.AddTransient(_ => new AdminCategoriesController(null!, null!)), role);
        using var client = StaffHttpQueryTests.Client(app);
        var id = ObjectId.GenerateNewId().ToString();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/categories", new { name = "Test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/admin/categories/{id}", new { name = "Test", version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/admin/categories/{id}/status", new { isActive = false, version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/admin/categories/{id}?version=1")).StatusCode);
    }
    [LocalMongoFact]
    public Task Unique_names_slugs_versions_and_aliases_survive_concurrency() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db); await database.CreateIndexesAsync(); var service = new CategoryService(database);
        var results = await Task.WhenAll(service.CreateAsync(new() { Name = "Ficci\u00f3n" }, default), service.CreateAsync(new() { Name = " FICCION " }, default));
        var category = Assert.Single(results, r => r.Success).Category!;
        Assert.Equal("category_duplicate", Assert.Single(results, r => !r.Success).ErrorCode);
        var rename = await service.UpdateAsync(category.Id, new() { Name = "Narrativa", Version = 1 }, default);
        Assert.True(rename.Success); Assert.Equal(category.Slug, rename.Category!.Slug); Assert.Equal(2, rename.Category.Version);
        Assert.Equal("category_version_conflict", (await service.StatusAsync(category.Id, new(false, 1), default)).ErrorCode);
        Assert.Equal("category_duplicate", (await service.CreateAsync(new() { Name = "Ficcion" }, default)).ErrorCode);
        var first = await service.CreateAsync(new() { Name = "A B" }, default);
        var second = await service.CreateAsync(new() { Name = "A-B" }, default);
        Assert.True(second.Success); Assert.NotEqual(first.Category!.Slug, second.Category!.Slug);
    });
    [LocalMongoFact]
    public Task Canonical_reads_filters_facets_legacy_and_inactive_rules_reconcile() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db); await database.CreateIndexesAsync(); var categories = new CategoryService(database);
        var c = (await categories.CreateAsync(new() { Name = "Ficci\u00f3n" }, default)).Category!;
        var store = new MongoBookStore(database); var service = new BookService(store, null!, categories);
        var id = ObjectId.GenerateNewId().ToString();
        Assert.True((await service.CreateAsync(Request([c.Id]), id, DateTime.UtcNow, default)).Success);
        await database.Books.InsertOneAsync(new Book { Id = ObjectId.GenerateNewId().ToString(), Genre = " FICCION ", IsActive = true });
        Assert.True((await categories.UpdateAsync(c.Id, new() { Name = "Narrativa", Version = 1 }, default)).Success);
        var page = await service.SearchAsync(new() { Genre = "ficci\u00f3n" }, false, default);
        Assert.Equal(2, page.TotalItems);
        Assert.Equal("Narrativa", (await service.GetDetailAsync(id, false, default))!.Genres.Single());
        Assert.Equal(2, (await service.SearchAsync(new() { CategoryId = c.Id }, false, default)).TotalItems);
        var facet = Assert.Single(await service.GetGenreFacetsAsync(default)); Assert.Equal(c.Id, facet.Id); Assert.Equal("Narrativa", facet.Value); Assert.Equal(2, facet.Count);
        await Assert.ThrowsAsync<CategoryReferenceException>(() => service.SearchAsync(new() { CategoryId = ObjectId.GenerateNewId().ToString(), Genre = "Ficcion" }, false, default));
        Assert.True((await categories.StatusAsync(c.Id, new(false, 2), default)).Success);
        Assert.Equal("category_inactive", (await service.CreateAsync(Request([c.Id]), ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, default)).ErrorCode);
        Assert.True((await service.UpdateAsync(id, Request([c.Id]) with { Title = "Changed" }, DateTime.UtcNow, default)).Success);
        Assert.Equal("category_in_use", (await categories.DeleteAsync(c.Id, 3, default)).ErrorCode);
        Assert.Equal("category_unknown_genre", (await service.CreateAsync(Request(null) with { Genres = ["Unknown"] }, ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, default)).ErrorCode);
        Assert.Equal("category_unknown_genre", (await service.CreateAsync(Request([c.Id]) with { Genres = ["Unknown"] }, ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, default)).ErrorCode);
        var other = (await categories.CreateAsync(new() { Name = "History" }, default)).Category!;
        Assert.Equal("category_contract_conflict", (await service.CreateAsync(Request([other.Id]) with { Genres = ["Narrativa"] }, ObjectId.GenerateNewId().ToString(), DateTime.UtcNow, default)).ErrorCode);
    });
    [LocalMongoFact]
    public Task Assignment_racing_delete_or_deactivation_never_leaves_orphans() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db); await database.CreateIndexesAsync(); var categories = new CategoryService(database); var store = new MongoBookStore(database);
        for (var i = 0; i < 16; i++)
        {
            var c = (await categories.CreateAsync(new() { Name = "Race " + i }, default)).Category!;
            var book = new Book { Id = ObjectId.GenerateNewId().ToString(), CategoryIds = [c.Id] };
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Insert() { await start.Task; try { await store.InsertAsync(book, default); } catch (CategoryReferenceException) { } }
            async Task Remove() { await start.Task; if (i % 2 == 0) await categories.DeleteAsync(c.Id, 1, default); else await categories.StatusAsync(c.Id, new(false, 1), default); }
            var insert = Insert(); var remove = Remove(); start.SetResult(); await Task.WhenAll(insert, remove);
            var exists = await database.Categories.Find(x => x.Id == c.Id).AnyAsync();
            var assigned = await database.Books.Find(x => x.CategoryIds.Contains(c.Id)).AnyAsync();
            Assert.False(assigned && !exists);
            if (!exists || i % 2 != 0) await Assert.ThrowsAsync<CategoryReferenceException>(() => store.InsertAsync(new Book { Id = ObjectId.GenerateNewId().ToString(), CategoryIds = [c.Id] }, default));
        }
    });
    [LocalMongoFact]
    public Task Admin_http_pagination_conflicts_audit_and_public_shape() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db); await database.CreateIndexesAsync(); var service = new CategoryService(database);
        var audit = new Logservice(database, NullLogger<Logservice>.Instance, new HttpContextAccessor());
        await using var app = await StaffHttpQueryTests.StartApp(s => { s.AddTransient(_ => new AdminCategoriesController(service, audit)); s.AddTransient(_ => new CategoriesController(service)); }, RoleNames.Admin);
        using var client = StaffHttpQueryTests.Client(app);
        var response = await client.PostAsJsonAsync("/api/admin/categories", new { name = "History", description = "Safe description" }); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var category = (await response.Content.ReadFromJsonAsync<CategoryResponse>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/categories", new { name = "history" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/admin/categories/{category.Id}", new { name = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/admin/categories/{category.Id}/status", new { isActive = false, version = 1 })).StatusCode);
        var json = await client.GetStringAsync("/api/categories"); Assert.DoesNotContain("bookCount", json); Assert.DoesNotContain(category.Id, json);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/admin/categories/{category.Id}?version=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/admin/categories/{category.Id}?version=2")).StatusCode);
        Assert.True(await database.LogEntries.Find(x => x.EventType == "category.deleted").AnyAsync());
        Assert.False(await database.LogEntries.Find(x => x.Message.Contains("Safe description")).AnyAsync());
        for (var i = 0; i < 23; i++) await service.CreateAsync(new() { Name = "Page " + i.ToString("D2") }, default);
        var page = await service.SearchAsync(new() { Page = 2, PageSize = 20 }, false, default); Assert.Equal(23, page.TotalItems); Assert.Equal(3, page.Items.Count);
        var publicPage = await client.GetStringAsync("/api/categories?query=Page%202&page=1&pageSize=2");
        using var parsed = System.Text.Json.JsonDocument.Parse(publicPage);
        Assert.Equal(3, parsed.RootElement.GetProperty("totalItems").GetInt32());
        Assert.Equal(2, parsed.RootElement.GetProperty("items").GetArrayLength());
    });
    private static BookWriteRequest Request(IReadOnlyList<string>? ids) => new() { Title = "Book", Authors = ["Author"], Description = "Description of at least twenty characters", CategoryIds = ids, TotalCopies = 2 };
}
