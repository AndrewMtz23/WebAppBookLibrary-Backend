using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class StaffReviewRegressionTests
{
    internal static async Task WithDatabase(Func<IMongoDatabase, Task> test)
    {
        var uri = Environment.GetEnvironmentVariable("BOOK_LIBRARY_TEST_MONGO_URI");
        Assert.Equal("mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa", uri);
        var client = new MongoClient(uri);
        var name = "booklibrary_review_test_" + Guid.NewGuid().ToString("N");
        try { await test(client.GetDatabase(name)); }
        finally { await client.DropDatabaseAsync(name); }
    }

    [LocalMongoFact]
    public Task Effective_loan_filters_and_sort_match_response_for_modern_and_raw_legacy_documents() => WithDatabase(async db =>
    {
        var now = DateTime.UtcNow;
        var old = now.AddDays(-30);
        var bookId = ObjectId.GenerateNewId().ToString();
        var userId = ObjectId.GenerateNewId().ToString();
        var raw = db.GetCollection<BsonDocument>("Loans");
        BsonDocument Legacy(bool returned) => new() { { "_id", ObjectId.GenerateNewId() }, { "BookId", bookId }, { "UserId", userId }, { "LoanDate", old }, { "IsReturned", returned } };
        var legacyActive = Legacy(false);
        var legacyReturned = Legacy(true); legacyReturned["ReturnDate"] = now.AddDays(-2);
        var modern = new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = bookId, UserId = userId, MediaType = "physical", Status = "active", ReservedAt = old.AddDays(1), DueAt = old.AddDays(16) }.ToBsonDocument();
        var future = new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = bookId, UserId = userId, MediaType = "physical", Status = "active", ReservedAt = now, DueAt = now.AddDays(10) }.ToBsonDocument();
        var documents = new[] { legacyActive, legacyReturned, modern, future };
        await raw.InsertManyAsync(documents);
        await db.GetCollection<Book>("Books").InsertOneAsync(new Book { Id = bookId, Title = "Reconcile" });
        var store = new MongoLoanStore(db.GetCollection<Book>("Books"), db.GetCollection<Loan>("Loans"), db.GetCollection<User>("Users"));
        var responses = documents.Select(d => LoanResponse.From(BsonSerializer.Deserialize<Loan>(d), now)).ToArray();
        foreach (var text in new string?[] { null, "Reconcile" })
        {
            foreach (var status in new[] { "active", "overdue", "returned", "outstanding" })
            {
                var expected = responses.Where(r => status == "outstanding" ? r.Status is "active" or "overdue" : r.Status == status).Select(r => r.Id).Order().ToArray();
                var page = await store.SearchAsync(new LoanQuery { Query = text, Status = status, MediaType = "physical" }.Normalize(), default);
                Assert.Equal(expected, page.Items.Select(l => l.Id).Order());
                Assert.Equal(expected.Length, page.TotalItems);
            }
            var due = await store.SearchAsync(new LoanQuery { Query = text, DueFrom = old.AddDays(14), DueTo = old.AddDays(17), Sort = "dueAt", Direction = "asc", PageSize = 1 }.Normalize(), default);
            Assert.Equal(3, due.TotalItems);
            Assert.Equal(old.AddDays(14).ToString("s"), LoanResponse.From(Assert.Single(due.Items), now).DueAt!.Value.ToString("s"));
            var returned = await store.SearchAsync(new LoanQuery { Query = text, DateField = "returnedAt", From = now.AddDays(-3), To = now.AddDays(-1) }.Normalize(), default);
            Assert.Equal(legacyReturned["_id"].ToString(), Assert.Single(returned.Items).Id);
            var reserved = await store.SearchAsync(new LoanQuery { Query = text, From = old.AddSeconds(-1), To = old.AddDays(1), Sort = "reservedAt", Direction = "asc" }.Normalize(), default);
            Assert.Equal(2, reserved.TotalItems);
        }
    });

    [LocalMongoFact]
    public Task Invalid_resource_authorities_are_missing_but_valid_https_is_not() => WithDatabase(async db =>
    {
        var invalid = new[] { "https://example.com:abc/file", "https://[bad]/file", "https://example.com:99999/file", "http://example.com/file", "" };
        var accepted = new[] { "https://example.com/file", "https://example.com:65535/file", "https://[::1]:443/file", "https://bücher.de/file", "https://reader@example.com/file", "https://example.com/a b" };
        var valid = accepted.Concat(accepted.Select(url => new Uri(url).AbsoluteUri)).Distinct().ToArray();
        Assert.All(valid, url => Assert.True(BookRules.IsAbsoluteHttps(url), url));
        Assert.All(invalid, url => Assert.False(BookRules.IsAbsoluteHttps(url), url));
        var books = db.GetCollection<Book>("Books");
        await books.InsertManyAsync(invalid.Concat(valid).Select(url => new Book { Id = ObjectId.GenerateNewId().ToString(), Title = url, MediaType = "digital", IsActive = true, DigitalResourceUrl = url }));
        var store = new MongoBookStore(books, db.GetCollection<Loan>("Loans"), db.GetCollection<Favorite>("Favorites"), db.GetCollection<User>("Users"));
        var page = await store.SearchAsync(new BookQuery { MissingResource = true }.Normalize(), true, null, default);
        Assert.Equal(invalid.Order(), page.Items.Select(i => i.Book.Title).Order());
        Assert.Equal(invalid.Length, page.TotalItems);
        var good = await store.SearchAsync(new BookQuery { MissingResource = false }.Normalize(), true, null, default);
        Assert.Equal(valid.Order(), good.Items.Select(i => i.Book.Title).Order());
        Assert.Equal(valid.Length, good.TotalItems);
        var next = await store.SearchAsync(new BookQuery { MissingResource = false, PageSize = 2, Page = 2, Sort = "title", Direction = "asc" }.Normalize(), true, null, default);
        Assert.Equal(valid.Length, next.TotalItems);
        Assert.Equal(2, next.Items.Count);
    });

    [LocalMongoFact]
    public Task Largest_pages_are_empty_with_exact_counts_in_all_query_paths() => WithDatabase(async db =>
    {
        var books = db.GetCollection<Book>("Books"); var loans = db.GetCollection<Loan>("Loans"); var users = db.GetCollection<User>("Users");
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Boundary", IsActive = true };
        await books.InsertOneAsync(book);
        await users.InsertOneAsync(new User { Id = ObjectId.GenerateNewId().ToString(), Username = "boundary" });
        await loans.InsertOneAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id });
        var bookStore = new MongoBookStore(books, loans, db.GetCollection<Favorite>("Favorites"), users);
        foreach (var sort in new[] { "title", "reservationCount" })
        {
            var page = await bookStore.SearchAsync(new BookQuery { Page = int.MaxValue, PageSize = 100, Sort = sort }.Normalize(), true, null, default);
            Assert.Empty(page.Items); Assert.Equal(1, page.TotalItems); Assert.Equal(int.MaxValue, page.Page);
        }
        var admin = await new MongoAdminUserStore(users).SearchAsync(new AdminUserQuery { Page = int.MaxValue, PageSize = 100 }, default);
        Assert.Empty(admin.Items); Assert.Equal(1, admin.TotalItems);
        foreach (var text in new string?[] { null, "Boundary" })
        {
            var page = await new MongoLoanStore(books, loans, users).SearchAsync(new LoanQuery { Page = int.MaxValue, PageSize = 100, Query = text }.Normalize(), default);
            Assert.Empty(page.Items); Assert.Equal(1, page.TotalItems);
        }
    });
}
