using Moq;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public class LoanServiceTests
{
    [Fact]
    public async Task CreateLoan_stops_when_atomic_reservation_loses()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.ReserveAvailableBookAsync("b1")).ReturnsAsync((Book?)null);
        var service = CreateService(store);

        var result = await service.CreateLoanAsync("b1", "ana");

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.BookUnavailable, result.ErrorCode);
        store.Verify(x => x.InsertLoanAsync(It.IsAny<Loan>()), Times.Never);
    }

    [Fact]
    public async Task CreateLoan_restores_book_when_insertion_fails()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.ReserveAvailableBookAsync("b1")).ReturnsAsync(Book("b1"));
        store.Setup(x => x.InsertLoanAsync(It.IsAny<Loan>()))
            .ThrowsAsync(new InvalidOperationException("write failed"));
        var service = CreateService(store);

        var result = await service.CreateLoanAsync("b1", "ana");

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.LoanPersistenceFailed, result.ErrorCode);
        store.Verify(x => x.RestoreBookAvailabilityAsync("b1"), Times.Once);
    }

    [Fact]
    public async Task ReturnLoan_rejects_user_who_does_not_own_loan()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.FindActiveLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u2"));
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.Forbidden, result.ErrorCode);
        store.Verify(x => x.MarkReturnedAsync(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
        store.Verify(x => x.RestoreBookAvailabilityAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(RoleNames.Librarian)]
    [InlineData(RoleNames.Admin)]
    public async Task ReturnLoan_allows_staff_to_return_another_users_loan(string role)
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("staff")).ReturnsAsync(User("staff-id", "staff"));
        store.Setup(x => x.FindActiveLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u2"));
        store.Setup(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>())).ReturnsAsync(true);
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "staff", role);

        Assert.True(result.Success);
        Assert.False(result.Idempotent);
        store.Verify(x => x.RestoreBookAvailabilityAsync("b1"), Times.Once);
    }

    [Fact]
    public async Task ReturnLoan_allows_user_to_return_owned_loan()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.FindActiveLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u1"));
        store.Setup(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>())).ReturnsAsync(true);
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.True(result.Success);
        Assert.False(result.Idempotent);
        store.Verify(x => x.RestoreBookAvailabilityAsync("b1"), Times.Once);
    }

    [Fact]
    public async Task ReturnLoan_rejects_noncanonical_role()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", "manager");

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.Forbidden, result.ErrorCode);
        store.Verify(x => x.FindActiveLoanAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReturnLoan_reports_repeated_return_as_idempotent_without_updating_twice()
    {
        var returnedLoan = Loan("l1", "b1", "u1", isReturned: true);
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.FindActiveLoanAsync("l1")).ReturnsAsync((Loan?)null);
        store.Setup(x => x.FindLoanAsync("l1")).ReturnsAsync(returnedLoan);
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.ErrorCode);
        Assert.True(result.Idempotent);
        store.Verify(x => x.MarkReturnedAsync(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
        store.Verify(x => x.RestoreBookAvailabilityAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReturnLoan_reports_lost_conditional_update_as_idempotent_without_restoring_twice()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.FindActiveLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u1"));
        store.Setup(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>())).ReturnsAsync(false);
        store.Setup(x => x.FindLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u1", isReturned: true));
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.True(result.Success);
        Assert.True(result.Idempotent);
        store.Verify(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>()), Times.Once);
        store.Verify(x => x.RestoreBookAvailabilityAsync(It.IsAny<string>()), Times.Never);
    }

    private static LoanService CreateService(Mock<ILoanStore> store)
    {
        return new LoanService(store.Object);
    }

    private static User User(string id, string username)
    {
        return new User
        {
            Id = id,
            Username = username,
            IsActive = true,
            Role = RoleNames.User
        };
    }

    private static Book Book(string id)
    {
        return new Book
        {
            Id = id,
            Title = "Concurrency in Practice",
            Author = "Test Author",
            IsAvailable = true
        };
    }

    private static Loan Loan(string id, string bookId, string userId, bool isReturned = false)
    {
        return new Loan
        {
            Id = id,
            BookId = bookId,
            UserId = userId,
            IsReturned = isReturned
        };
    }
}
