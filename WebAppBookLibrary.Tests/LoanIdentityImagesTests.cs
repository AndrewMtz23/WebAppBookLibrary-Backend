using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class LoanIdentityImagesTests
{
    [LocalMongoFact]
    public Task List_and_detail_include_images_and_tolerate_missing_related_records() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var books = db.GetCollection<Book>("Books"); var users = db.GetCollection<User>("Users"); var loans = db.GetCollection<Loan>("Loans");
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), Title = "Portada", CoverUrl = "https://example.test/cover.jpg" };
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "reader", DisplayName = "Reader", AvatarUrl = "https://example.test/avatar.jpg" };
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id, UserId = user.Id, ReservedAt = DateTime.UtcNow, Status = "active", MediaType = "physical" };
        await books.InsertOneAsync(book); await users.InsertOneAsync(user); await loans.InsertOneAsync(loan);
        var service = new LoanService(new MongoLoanStore(books, loans, users));
        var page = await service.SearchAsync(new LoanQuery(), default);
        var detail = await service.GetDetailAsync(loan.Id, default);
        Assert.Equal(book.CoverUrl, Assert.Single(page.Items).BookCoverUrl);
        Assert.Equal(user.AvatarUrl, page.Items.Single().UserAvatarUrl);
        Assert.Equal(book.CoverUrl, detail!.Loan.BookCoverUrl); Assert.Equal(user.AvatarUrl, detail.Loan.UserAvatarUrl);
        await books.DeleteOneAsync(b => b.Id == book.Id); await users.DeleteOneAsync(u => u.Id == user.Id);
        var missing = Assert.Single((await service.SearchAsync(new LoanQuery(), default)).Items);
        Assert.Null(missing.BookCoverUrl); Assert.Null(missing.UserAvatarUrl);
        Assert.Null((await service.GetDetailAsync(loan.Id, default))!.Loan.BookTitle);
    });
}
