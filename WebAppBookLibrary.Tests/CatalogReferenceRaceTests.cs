using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class CatalogReferenceRaceTests
{
    [LocalMongoFact]
    public Task Inflight_reference_inserts_cannot_outlive_deletion() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db);
        var books = new MongoBookStore(database);
        var favorites = new MongoFavoriteStore(database);
        var loans = new MongoLoanStore(database);
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var id = ObjectId.GenerateNewId().ToString();
            await database.Books.InsertOneAsync(new Book { Id = id, MediaType = "digital", IsActive = true });
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task AddReference()
            {
                await start.Task;
                try
                {
                    if (iteration % 2 == 0) await favorites.InsertAsync(new Favorite { Id = ObjectId.GenerateNewId().ToString(), BookId = id, UserId = ObjectId.GenerateNewId().ToString() }, default);
                    else await loans.InsertLoanAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = id, UserId = ObjectId.GenerateNewId().ToString(), MediaType = "digital" }, default);
                }
                catch (BookReferenceUnavailableException) { }
            }
            async Task Delete()
            {
                await start.Task;
                await books.SetActiveAsync(id, false, DateTime.UtcNow, default);
                await books.DeletePermanentlyAsync(id, default);
            }
            var insert = AddReference(); var delete = Delete(); start.SetResult();
            await Task.WhenAll(insert, delete);
            var exists = await database.Books.Find(b => b.Id == id).AnyAsync();
            var referenced = await database.Favorites.Find(f => f.BookId == id).AnyAsync() || await database.Loans.Find(l => l.BookId == id).AnyAsync();
            Assert.False(referenced && !exists);
            // A stale caller arriving after a successful delete must be refused too.
            if (!exists)
                await Assert.ThrowsAsync<BookReferenceUnavailableException>(() => loans.InsertLoanAsync(new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = id, UserId = ObjectId.GenerateNewId().ToString(), MediaType = "digital" }, default));
        }
    });

    [LocalMongoFact]
    public Task Metadata_update_preserves_inflight_physical_inventory_and_refuses_media_change() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var database = new MongoDBService(db);
        var store = new MongoBookStore(database);
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Book", MediaType = "physical", TotalCopies = 3, AvailableCopies = 2 };
        await database.Books.InsertOneAsync(book); // One copy reserved before loan insertion completes.
        var service = new BookService(store);
        var request = new BookWriteRequest { Title = "Edited", Authors = ["Author"], Genres = ["Genre"], Description = "Description with at least twenty characters", MediaType = "physical", TotalCopies = 4 };
        Assert.True((await service.UpdateAsync(book.Id, request, DateTime.UtcNow, default)).Success);
        Assert.Equal(3, (await store.FindByIdAsync(book.Id, default))!.AvailableCopies);
        Assert.False((await service.UpdateAsync(book.Id, request with { MediaType = "digital", TotalCopies = null, DigitalResourceUrl = new Uri("https://example.org/book.pdf") }, DateTime.UtcNow, default)).Success);
    });
}
