using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Admin;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Loans;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class StaffMongoPersistenceTests
{
    [LocalMongoFact]
    [Trait("Category", "LocalMongo")]
    public async Task Operational_filters_sort_joins_and_out_of_range_metadata_execute_on_Mongo()
    {
        var uri = Environment.GetEnvironmentVariable("BOOK_LIBRARY_TEST_MONGO_URI");
        Assert.Equal("mongodb://127.0.0.1:27184/?replicaSet=booklibraryqa", uri);
        var client = new MongoClient(uri);
        var name = "booklibrary_phase4_test_" + Guid.NewGuid().ToString("N");
        var db = client.GetDatabase(name);
        try
        {
            var books = db.GetCollection<Book>("Books"); var loans = db.GetCollection<Loan>("Loans"); var users = db.GetCollection<User>("Users"); var favorites = db.GetCollection<Favorite>("Favorites");
            var now = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
            var anaId = ObjectId.GenerateNewId().ToString(); var bookId = ObjectId.GenerateNewId().ToString();
            await users.InsertManyAsync([
                new User { Id = anaId, Username = "ana.reader", DisplayName = "Ana Reader", Email = "ana@example.test", Role = RoleNames.User, IsActive = true, CreatedAt = now.AddDays(-5), LastLoginAt = now.AddDays(-1) },
                new User { Id = ObjectId.GenerateNewId().ToString(), Username = "zoe", DisplayName = "Zoe", Email = "zoe@example.test", Role = RoleNames.Librarian, IsActive = true, CreatedAt = now.AddDays(-2) }
            ]);
            await books.InsertManyAsync([
                new Book { Id = bookId, Title = "Operational Mongo", Authors = ["A"], MediaType = MediaTypes.Physical, IsActive = true, TotalCopies = 3, AvailableCopies = 1, CreatedAt = now },
                new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Broken Digital", Authors = ["B"], MediaType = MediaTypes.Digital, IsActive = true, DigitalResourceUrl = "http://unsafe", CreatedAt = now }
            ]);
            await loans.InsertOneAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = bookId, UserId = anaId, MediaType = MediaTypes.Physical, Status = LoanStatuses.Active, ReservedAt = now, DueAt = now.AddDays(14) });

            var bookStore = new MongoBookStore(books, loans, favorites, users);
            Assert.Equal("Operational Mongo", Assert.Single((await bookStore.SearchAsync(new BookQuery { LowStock = true }.Normalize(), true, null, default)).Items).Book.Title);
            Assert.Equal("Broken Digital", Assert.Single((await bookStore.SearchAsync(new BookQuery { MissingResource = true }.Normalize(), true, null, default)).Items).Book.Title);

            var adminPage = await new MongoAdminUserStore(users).SearchAsync(new AdminUserQuery { CreatedFrom = now.AddDays(-6), CreatedTo = now, Sort = "createdAt", Direction = "desc" }, default);
            Assert.Equal(["zoe", "ana.reader"], adminPage.Items.Select(x => x.Username));

            var loanPage = await new MongoLoanStore(books, loans, users).SearchDetailsAsync(new LoanQuery { Query = "Ana", Status = "outstanding", DueFrom = now.AddDays(13), DueTo = now.AddDays(15), Sort = "dueAt" }.Normalize(), default);
            var row = Assert.Single(loanPage.Items);
            Assert.Equal("Operational Mongo", row.BookTitle); Assert.Equal("ana.reader", row.Username); Assert.Equal("Ana Reader", row.DisplayName);
            var empty = await new MongoLoanStore(books, loans, users).SearchDetailsAsync(new LoanQuery { Page = 50, PageSize = 2 }.Normalize(), default);
            Assert.Empty(empty.Items); Assert.Equal(1, empty.TotalItems); Assert.Equal(50, empty.Page);
        }
        finally { await client.DropDatabaseAsync(name); }
    }
}

public sealed class LocalMongoFactAttribute : FactAttribute
{
    public LocalMongoFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BOOK_LIBRARY_TEST_MONGO_URI")))
            Skip = "Set BOOK_LIBRARY_TEST_MONGO_URI to the isolated loopback replica set to run persistence checks.";
    }
}
