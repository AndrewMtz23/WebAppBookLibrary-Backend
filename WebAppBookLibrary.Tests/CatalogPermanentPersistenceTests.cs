using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class CatalogPermanentPersistenceTests
{
    [LocalMongoFact]
    public Task Permanent_requires_inactive_and_refuses_any_loan_or_favorite() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var books = db.GetCollection<Book>("Books");
        var loans = db.GetCollection<Loan>("Loans");
        var favorites = db.GetCollection<Favorite>("Favorites");
        var store = new MongoBookStore(books, loans, favorites, db.GetCollection<User>("Users"));
        await using var app = await CatalogManagementTests.StartApp(store, RoleNames.Admin);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var kind in new[] { "active", "loan", "favorite", "clear" })
        {
            var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = kind, IsActive = kind == "active" };
            await books.InsertOneAsync(book);
            if (kind == "loan") await loans.InsertOneAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id, UserId = ObjectId.GenerateNewId().ToString(), Status = "returned", IsReturned = true });
            if (kind == "favorite") await favorites.InsertOneAsync(new Favorite { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id, UserId = ObjectId.GenerateNewId().ToString() });
            var response = await client.DeleteAsync($"/api/books/{book.Id}/permanent");
            Assert.Equal(kind == "clear" ? HttpStatusCode.NoContent : HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(kind != "clear", await books.Find(x => x.Id == book.Id).AnyAsync());
        }
    });
}
