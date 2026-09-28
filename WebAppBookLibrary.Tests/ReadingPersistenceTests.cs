using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Reading;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class ReadingPersistenceTests
{
    private static readonly DateTime Now = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static SaveReadingRequest Request(int percent = 25, string? revision = null) => new("reading", "percent", percent, null, revision);
    internal static async Task<(MongoDBService Db, MongoReadingStore Store, User User, Book Book)> Seed(IMongoDatabase db)
    {
        var mongo = new MongoDBService(db); await mongo.CreateIndexesAsync();
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "reading", Email = "reading@example.invalid", IsActive = true, Role = "user" };
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Visible title", PageCount = 1000, IsActive = true, DigitalResourceUrl = "https://example.invalid/private" };
        await mongo.Users.InsertOneAsync(user); await mongo.Books.InsertOneAsync(book);
        return (mongo, new MongoReadingStore(mongo), user, book);
    }

    [LocalMongoFact]
    public Task Concurrent_create_and_update_have_one_winner_and_do_not_touch_circulation() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var creates = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default)));
        Assert.Equal(new[] { 200, 409 }, creates.Select(x => x.Status).Order());
        var revision = creates.Single(x => x.Status == 200).Entry!.Revision;
        var updates = await Task.WhenAll(new[] { 50, 75 }.Select(p => f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(p, revision), Now.AddDays(1), default)));
        Assert.Equal(new[] { 200, 409 }, updates.Select(x => x.Status).Order());
        Assert.Equal(0, await f.Db.Loans.CountDocumentsAsync(FilterDefinition<Loan>.Empty));
        Assert.Equal(0, await f.Db.Favorites.CountDocumentsAsync(FilterDefinition<Favorite>.Empty));
        Assert.Equal(2, await f.Db.LogEntries.CountDocumentsAsync(FilterDefinition<LogEntry>.Empty));
    });

    [LocalMongoFact]
    public Task Old_revision_cannot_update_or_delete_recreated_entry() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var first = (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default)).Entry!;
        Assert.Equal(204, (await f.Store.DeleteAsync(f.User.Id, f.Book.Id, first.Revision, default)).Status);
        Assert.Equal(204, (await f.Store.DeleteAsync(f.User.Id, f.Book.Id, first.Revision, default)).Status);
        var second = (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default)).Entry!;
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal(409, (await f.Store.DeleteAsync(f.User.Id, f.Book.Id, first.Revision, default)).Status);
        Assert.Equal(409, (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(80, first.Revision), Now, default)).Status);
        Assert.Equal(400, (await f.Store.DeleteAsync(f.User.Id, f.Book.Id, "bad", default)).Status);
    });

    [LocalMongoFact]
    public Task Lists_are_private_paged_and_latest_excludes_unavailable_books() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default);
        var next = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Second", IsActive = true };
        await f.Db.Books.InsertOneAsync(next);
        var later = (await f.Store.SaveAsync(f.User.Id, next.Id, Request(50), Now.AddDays(1), default)).Entry!;
        Assert.Equal(next.Id, (await f.Store.LatestAsync(f.User.Id, default))!.BookId);
        var page = await f.Store.ListAsync(f.User.Id, new() { PageSize = 1 }, default);
        Assert.Single(page.Items); Assert.Equal(2, page.TotalItems); Assert.Equal(2, page.Counts.Reading);
        Assert.Equal(f.Book.Id, Assert.Single((await f.Store.ListAsync(f.User.Id, new() { PageSize = 1, Page = 2 }, default)).Items).BookId);
        Assert.Empty((await f.Store.ListAsync(ObjectId.GenerateNewId().ToString(), new(), default)).Items);
        Assert.Empty((await f.Store.ListAsync(f.User.Id, new() { Page = int.MaxValue }, default)).Items);
        await f.Db.Books.UpdateOneAsync(b => b.Id == next.Id, Builders<Book>.Update.Set(b => b.IsActive, false));
        Assert.Equal(f.Book.Id, (await f.Store.LatestAsync(f.User.Id, default))!.BookId);
        var hidden = Assert.Single((await f.Store.ListAsync(f.User.Id, new() { BookId = next.Id }, default)).Items);
        Assert.Equal("Libro no disponible", hidden.Title); Assert.False(hidden.BookAvailable); Assert.Null(hidden.CoverUrl);
        Assert.Equal(404, (await f.Store.SaveAsync(f.User.Id, next.Id, Request(60, later.Revision), Now, default)).Status);
        Assert.Equal(204, (await f.Store.DeleteAsync(f.User.Id, next.Id, later.Revision, default)).Status);
    });

    [LocalMongoFact]
    public Task Visibility_changes_and_account_deletion_serialize_with_reading_writes() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var books = new MongoBookStore(f.Db);
        var writes = f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default);
        await books.SetActiveAsync(f.Book.Id, false, Now, default);
        Assert.Contains((await writes).Status, new[] { 200, 404 });
        Assert.Equal(404, (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default)).Status);
        Assert.True((await books.DeletePermanentlyAsync(f.Book.Id, default)).Success);
        Assert.Null(await f.Store.LatestAsync(f.User.Id, default));
        var list = await f.Store.ListAsync(f.User.Id, new(), default);
        Assert.All(list.Items, item => Assert.False(item.BookAvailable));
        var admin = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "admin", Email = "admin@example.invalid", Role = "admin", IsActive = true };
        await f.Db.Users.InsertOneAsync(admin);
        var users = new MongoAdminUserStore(f.Db);
        await users.SetStatusSafelyAsync(admin.Id, f.User.Id, false, Now, default);
        await users.DeletePermanentlyAsync(admin.Id, f.User.Id, default);
        Assert.Equal(401, (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default)).Status);
    });

    [LocalMongoFact]
    public Task Account_removal_cleans_only_its_reading_history_even_with_a_pending_save() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var admin = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "owner", Email = "owner@example.invalid", Role = "admin", IsActive = true };
        await f.Db.Users.InsertOneAsync(admin);
        var saved = (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default)).Entry!;
        await f.Store.SaveAsync(admin.Id, f.Book.Id, Request(), Now, default);
        var users = new MongoAdminUserStore(f.Db);
        var writing = f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(70, saved.Revision), Now.AddMinutes(1), default);
        await users.SetStatusSafelyAsync(admin.Id, f.User.Id, false, Now, default);
        await users.DeletePermanentlyAsync(admin.Id, f.User.Id, default);
        Assert.Contains((await writing).Status, new[] { 200, 401 });
        Assert.Null(await f.Db.Users.Find(u => u.Id == f.User.Id).FirstOrDefaultAsync());
        Assert.Equal(0, await f.Db.ReadingEntries.CountDocumentsAsync(e => e.UserId == f.User.Id));
        Assert.Equal(1, await f.Db.ReadingEntries.CountDocumentsAsync(e => e.UserId == admin.Id));
    });

    [LocalMongoFact]
    public Task Inactive_account_cannot_mutate_and_repeated_save_has_no_extra_audit() => StaffReviewRegressionTests.WithDatabase(async db => {
        var f = await Seed(db);
        var saved = (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(), Now, default)).Entry!;
        var repeat = (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(25, saved.Revision), Now.AddDays(1), default)).Entry!;
        Assert.Equal(saved, repeat); Assert.Equal(1, await f.Db.LogEntries.CountDocumentsAsync(FilterDefinition<LogEntry>.Empty));
        await f.Db.Users.UpdateOneAsync(u => u.Id == f.User.Id, Builders<User>.Update.Set(u => u.IsActive, false));
        Assert.Equal(401, (await f.Store.SaveAsync(f.User.Id, f.Book.Id, Request(50, saved.Revision), Now, default)).Status);
        Assert.Equal(401, (await f.Store.DeleteAsync(f.User.Id, f.Book.Id, saved.Revision, default)).Status);
    });
}
