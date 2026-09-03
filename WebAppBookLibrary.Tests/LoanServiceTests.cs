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
        store.Setup(x => x.ReserveAvailableBookAsync("b1", It.IsAny<string>()))
            .ReturnsAsync((Book?)null);
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
        store.Setup(x => x.ReserveAvailableBookAsync("b1", It.IsAny<string>()))
            .ReturnsAsync(Book("b1"));
        store.Setup(x => x.InsertLoanAsync(It.IsAny<Loan>()))
            .ThrowsAsync(new InvalidOperationException("write failed"));
        store.Setup(x => x.RestoreBookAvailabilityAsync("b1", It.IsAny<string>(), false))
            .ReturnsAsync(true);
        var service = CreateService(store);

        var result = await service.CreateLoanAsync("b1", "ana");

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.LoanPersistenceFailed, result.ErrorCode);
        store.Verify(
            x => x.RestoreBookAvailabilityAsync("b1", It.IsAny<string>(), false),
            Times.Once);
    }

    [Fact]
    public async Task CreateLoan_reports_distinct_error_when_insert_and_rollback_both_throw()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.ReserveAvailableBookAsync("b1", It.IsAny<string>()))
            .ReturnsAsync(Book("b1"));
        store.Setup(x => x.InsertLoanAsync(It.IsAny<Loan>()))
            .ThrowsAsync(new InvalidOperationException("insert failed"));
        store.Setup(x => x.RestoreBookAvailabilityAsync("b1", It.IsAny<string>(), false))
            .ThrowsAsync(new InvalidOperationException("restore failed"));
        var service = CreateService(store);

        var result = await service.CreateLoanAsync("b1", "ana");

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.ReservationRollbackFailed, result.ErrorCode);
    }

    [Fact]
    public async Task CreateLoan_reports_distinct_error_when_rollback_returns_false()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.ReserveAvailableBookAsync("b1", It.IsAny<string>()))
            .ReturnsAsync(Book("b1"));
        store.Setup(x => x.InsertLoanAsync(It.IsAny<Loan>()))
            .ThrowsAsync(new InvalidOperationException("insert failed"));
        store.Setup(x => x.RestoreBookAvailabilityAsync("b1", It.IsAny<string>(), false))
            .ReturnsAsync(false);
        var service = CreateService(store);

        var result = await service.CreateLoanAsync("b1", "ana");

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.ReservationRollbackFailed, result.ErrorCode);
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
        store.Verify(
            x => x.RestoreBookAvailabilityAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>()),
            Times.Never);
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
        store.Setup(x => x.RestoreBookAvailabilityAsync("b1", "l1", true)).ReturnsAsync(true);
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "staff", role);

        Assert.True(result.Success);
        Assert.False(result.Idempotent);
        store.Verify(x => x.RestoreBookAvailabilityAsync("b1", "l1", true), Times.Once);
    }

    [Fact]
    public async Task ReturnLoan_allows_user_to_return_owned_loan()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.FindActiveLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u1"));
        store.Setup(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>())).ReturnsAsync(true);
        store.Setup(x => x.RestoreBookAvailabilityAsync("b1", "l1", true)).ReturnsAsync(true);
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.True(result.Success);
        Assert.False(result.Idempotent);
        store.Verify(x => x.RestoreBookAvailabilityAsync("b1", "l1", true), Times.Once);
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
        store.Setup(x => x.RestoreBookAvailabilityAsync("b1", "l1", false)).ReturnsAsync(true);
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.ErrorCode);
        Assert.True(result.Idempotent);
        store.Verify(x => x.MarkReturnedAsync(It.IsAny<string>(), It.IsAny<DateTime>()), Times.Never);
        store.Verify(x => x.RestoreBookAvailabilityAsync("b1", "l1", false), Times.Once);
    }

    [Fact]
    public async Task ReturnLoan_repairs_availability_after_losing_conditional_update()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.FindActiveLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u1"));
        store.Setup(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>())).ReturnsAsync(false);
        store.Setup(x => x.FindLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u1", isReturned: true));
        store.Setup(x => x.RestoreBookAvailabilityAsync("b1", "l1", false)).ReturnsAsync(true);
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.True(result.Success);
        Assert.True(result.Idempotent);
        store.Verify(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>()), Times.Once);
        store.Verify(x => x.RestoreBookAvailabilityAsync("b1", "l1", false), Times.Once);
    }

    [Fact]
    public async Task ReturnLoan_reports_book_restore_failure_when_store_returns_false()
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.Setup(x => x.FindActiveLoanAsync("l1")).ReturnsAsync(Loan("l1", "b1", "u1"));
        store.Setup(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>())).ReturnsAsync(true);
        store.Setup(x => x.RestoreBookAvailabilityAsync("b1", "l1", true)).ReturnsAsync(false);
        var service = CreateService(store);

        var result = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.BookRestoreFailed, result.ErrorCode);
    }

    [Fact]
    public async Task ReturnLoan_retries_restore_after_mark_succeeded_and_first_restore_threw()
    {
        var activeLoan = Loan("l1", "b1", "u1");
        var returnedLoan = Loan("l1", "b1", "u1", isReturned: true);
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.SetupSequence(x => x.FindActiveLoanAsync("l1"))
            .ReturnsAsync(activeLoan)
            .ReturnsAsync((Loan?)null);
        store.Setup(x => x.FindLoanAsync("l1")).ReturnsAsync(returnedLoan);
        store.Setup(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>())).ReturnsAsync(true);
        store.SetupSequence(x => x.RestoreBookAvailabilityAsync("b1", "l1", It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("restore failed"))
            .ReturnsAsync(true);
        var service = CreateService(store);

        var first = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);
        var retry = await service.MarkAsReturnedAsync("l1", "ana", RoleNames.User);

        Assert.False(first.Success);
        Assert.Equal(LoanOperationErrorCodes.BookRestoreFailed, first.ErrorCode);
        Assert.True(retry.Success);
        Assert.True(retry.Idempotent);
        store.Verify(x => x.MarkReturnedAsync("l1", It.IsAny<DateTime>()), Times.Once);
        store.Verify(
            x => x.RestoreBookAvailabilityAsync("b1", "l1", It.IsAny<bool>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task ReturnLoan_retry_does_not_release_book_reserved_by_newer_loan()
    {
        const string firstLoanId = "l1";
        string? activeLoanId = firstLoanId;
        var bookIsAvailable = false;
        var firstLoan = Loan(firstLoanId, "b1", "u1");
        var returnedFirstLoan = Loan(firstLoanId, "b1", "u1", isReturned: true);
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(User("u1", "ana"));
        store.SetupSequence(x => x.FindActiveLoanAsync(firstLoanId))
            .ReturnsAsync(firstLoan)
            .ReturnsAsync((Loan?)null);
        store.Setup(x => x.FindLoanAsync(firstLoanId)).ReturnsAsync(returnedFirstLoan);
        store.Setup(x => x.MarkReturnedAsync(firstLoanId, It.IsAny<DateTime>()))
            .ReturnsAsync(true);
        store.Setup(x => x.RestoreBookAvailabilityAsync(
                "b1",
                firstLoanId,
                It.IsAny<bool>()))
            .ReturnsAsync((string _, string loanId, bool allowLegacyUncorrelated) =>
            {
                var matchesCurrentLoan = activeLoanId == loanId;
                var matchesSafeNullState = activeLoanId is null &&
                    (bookIsAvailable || allowLegacyUncorrelated);
                if (!matchesCurrentLoan && !matchesSafeNullState)
                    return false;

                bookIsAvailable = true;
                activeLoanId = null;
                return true;
            });
        store.Setup(x => x.ReserveAvailableBookAsync("b1", It.IsAny<string>()))
            .ReturnsAsync((string _, string loanId) =>
            {
                if (!bookIsAvailable)
                    return null;

                bookIsAvailable = false;
                activeLoanId = loanId;
                return Book("b1");
            });
        var service = CreateService(store);

        var firstReturn = await service.MarkAsReturnedAsync(firstLoanId, "ana", RoleNames.User);
        var secondLoan = await service.CreateLoanAsync("b1", "ana");
        var retriedFirstReturn = await service.MarkAsReturnedAsync(firstLoanId, "ana", RoleNames.User);

        Assert.True(firstReturn.Success);
        Assert.True(secondLoan.Success);
        Assert.NotNull(secondLoan.Loan);
        Assert.False(string.IsNullOrWhiteSpace(secondLoan.Loan.Id));
        Assert.Equal(secondLoan.Loan.Id, activeLoanId);
        Assert.False(bookIsAvailable);
        Assert.False(retriedFirstReturn.Success);
        Assert.Equal(LoanOperationErrorCodes.BookRestoreFailed, retriedFirstReturn.ErrorCode);
        store.Verify(
            x => x.ReserveAvailableBookAsync("b1", secondLoan.Loan.Id),
            Times.Once);
        store.Verify(
            x => x.RestoreBookAvailabilityAsync("b1", secondLoan.Loan.Id, It.IsAny<bool>()),
            Times.Never);
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
