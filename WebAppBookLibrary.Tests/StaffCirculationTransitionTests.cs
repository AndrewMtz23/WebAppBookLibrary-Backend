using Moq;
using MongoDB.Bson;
using MongoDB.Driver;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;
public sealed class StaffCirculationTransitionTests
{
    [Fact]
    public async Task Concurrent_opposite_completion_returns_transition_conflict()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("staff")).ReturnsAsync(new User { Id = "staff", IsActive = true });
        store.SetupSequence(x => x.FindLoanAsync("loan", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Loan { Id = "loan", BookId = "book", Status = "active", MediaType = "physical" })
            .ReturnsAsync(new Loan { Id = "loan", BookId = "book", Status = "cancelled", MediaType = "physical" });
        var result = await new LoanService(store.Object).ReturnReservationAsync("loan", "staff", RoleNames.Librarian, DateTime.UtcNow, default);
        Assert.Equal(LoanOperationErrorCodes.InvalidTransition, result.ErrorCode);
    }

    [LocalMongoFact]
    public Task Legacy_blank_status_remains_operable_and_retry_never_releases_twice() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var books = db.GetCollection<Book>("Books"); var users = db.GetCollection<User>("Users"); var loans = db.GetCollection<Loan>("Loans");
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), MediaType = "physical", TotalCopies = 1, AvailableCopies = 0 };
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "staff", IsActive = true };
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id, UserId = user.Id, Status = " ", MediaType = "", LoanDate = DateTime.UtcNow.AddDays(-20) };
        await books.InsertOneAsync(book); await users.InsertOneAsync(user); await loans.InsertOneAsync(loan);
        var service = new LoanService(new MongoLoanStore(books, loans, users));
        Assert.True((await service.ReturnReservationAsync(loan.Id, "staff", RoleNames.Librarian, DateTime.UtcNow, default)).Success);
        Assert.True((await service.ReturnReservationAsync(loan.Id, "staff", RoleNames.Admin, DateTime.UtcNow, default)).Idempotent);
        Assert.Equal(LoanOperationErrorCodes.InvalidTransition, (await service.CancelReservationAsync(loan.Id, "staff", RoleNames.Admin, DateTime.UtcNow, default)).ErrorCode);
        Assert.Equal(1, (await books.Find(b => b.Id == book.Id).SingleAsync()).AvailableCopies);
    });

    [LocalMongoFact]
    public Task Real_return_cancel_race_releases_one_copy_and_detail_matches_winner() => StaffReviewRegressionTests.WithDatabase(async db =>
    {
        var books = db.GetCollection<Book>("Books"); var users = db.GetCollection<User>("Users"); var loans = db.GetCollection<Loan>("Loans");
        var book = new Book { Id = ObjectId.GenerateNewId().ToString(), MediaType = "physical", TotalCopies = 1, AvailableCopies = 0 };
        var user = new User { Id = ObjectId.GenerateNewId().ToString(), Username = "staff", IsActive = true };
        var loan = new Loan { Id = ObjectId.GenerateNewId().ToString(), BookId = book.Id, UserId = user.Id, Status = "active", MediaType = "physical", ReservedAt = DateTime.UtcNow.AddDays(-1) };
        await books.InsertOneAsync(book); await users.InsertOneAsync(user); await loans.InsertOneAsync(loan);
        var service = new LoanService(new MongoLoanStore(books, loans, users));
        var results = await Task.WhenAll(service.ReturnReservationAsync(loan.Id, "staff", RoleNames.Admin, DateTime.UtcNow, default), service.CancelReservationAsync(loan.Id, "staff", RoleNames.Librarian, DateTime.UtcNow, default));
        Assert.Single(results, r => r.Success);
        Assert.Equal(1, (await books.Find(b => b.Id == book.Id).SingleAsync()).AvailableCopies);
        var detail = await service.GetDetailAsync(loan.Id, default);
        Assert.Contains(detail!.History, h => h.EventType == detail.Loan.Status && h.Source == "recorded-date");
        Assert.DoesNotContain(detail.History, h => h.Source == "audit");
        var again = detail.Loan.Status == "returned" ? await service.ReturnReservationAsync(loan.Id, "staff", RoleNames.Admin, DateTime.UtcNow, default) : await service.CancelReservationAsync(loan.Id, "staff", RoleNames.Librarian, DateTime.UtcNow, default);
        Assert.True(again.Idempotent);
        Assert.Equal(1, (await books.Find(b => b.Id == book.Id).SingleAsync()).AvailableCopies);
    });
}
