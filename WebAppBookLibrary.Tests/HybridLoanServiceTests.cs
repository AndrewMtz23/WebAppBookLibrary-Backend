using Moq;
using WebAppBookLibrary.Contracts.Loans;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Loans;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Security;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class HybridLoanServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReserveAsync_PhysicalBookDecrementsInventoryAndSetsDueDate()
    {
        var store = StoreFor(Book(MediaTypes.Physical));
        store.Setup(x => x.TryDecrementPhysicalInventoryAsync("b1", Now, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new LoanService(store.Object);

        var result = await service.ReserveAsync("b1", "ana", "u1", Now, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Now.AddDays(14), result.Loan!.DueAt);
        Assert.Equal(MediaTypes.Physical, result.Loan.MediaType);
        store.Verify(x => x.TryDecrementPhysicalInventoryAsync("b1", Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReserveAsync_DigitalBookDoesNotMutateInventoryAndHasNoDueDate()
    {
        var store = StoreFor(Book(MediaTypes.Digital));
        var service = new LoanService(store.Object);

        var result = await service.ReserveAsync("b1", "ana", "u1", Now, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Loan!.DueAt);
        store.Verify(x => x.TryDecrementPhysicalInventoryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReserveAsync_RejectsDuplicateActiveReservation()
    {
        var store = StoreFor(Book(MediaTypes.Digital));
        store.Setup(x => x.HasActiveReservationAsync("u1", "b1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new LoanService(store.Object);

        var result = await service.ReserveAsync("b1", "ana", "u1", Now, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.DuplicateActive, result.ErrorCode);
        store.Verify(x => x.InsertLoanAsync(It.IsAny<Loan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReturnReservationAsync_IncrementsPhysicalInventoryOnlyAfterTransition()
    {
        var loan = ActiveLoan(MediaTypes.Physical);
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindLoanAsync("l1", It.IsAny<CancellationToken>())).ReturnsAsync(loan);
        store.Setup(x => x.CompletePhysicalAsync("l1", "b1", LoanStatuses.Returned, Now, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new LoanService(store.Object);

        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(new User { Id = "u1", Username = "ana", IsActive = true });
        var result = await service.ReturnReservationAsync("l1", "ana", RoleNames.User, Now, CancellationToken.None);

        Assert.True(result.Success);
        store.Verify(x => x.CompletePhysicalAsync("l1", "b1", LoanStatuses.Returned, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReturnReservationAsync_RepeatedReturnIsIdempotentWithoutIncrementingAgain()
    {
        var loan = ActiveLoan(MediaTypes.Physical);
        loan.Status = LoanStatuses.Returned;
        loan.ReturnedAt = Now.AddMinutes(-1);
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindLoanAsync("l1", It.IsAny<CancellationToken>())).ReturnsAsync(loan);
        var service = new LoanService(store.Object);

        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(new User { Id = "u1", Username = "ana", IsActive = true });
        var result = await service.ReturnReservationAsync("l1", "ana", RoleNames.User, Now, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Idempotent);
        store.Verify(x => x.TryIncrementPhysicalInventoryAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelReservationAsync_RejectsTransitionFromReturned()
    {
        var loan = ActiveLoan(MediaTypes.Physical);
        loan.Status = LoanStatuses.Returned;
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindLoanAsync("l1", It.IsAny<CancellationToken>())).ReturnsAsync(loan);
        var service = new LoanService(store.Object);

        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(new User { Id = "u1", Username = "ana", IsActive = true });
        var result = await service.CancelReservationAsync("l1", "ana", RoleNames.User, Now, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.InvalidTransition, result.ErrorCode);
    }

    [Fact]
    public async Task GetDigitalAccessAsync_RequiresActiveReservation()
    {
        var book = Book(MediaTypes.Digital);
        book.DigitalResourceUrl = "https://cdn.example/private.pdf";
        var store = StoreFor(book);
        store.Setup(x => x.HasActiveReservationAsync("u1", "b1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var service = new LoanService(store.Object);

        var result = await service.GetDigitalAccessAsync("b1", "ana", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(LoanOperationErrorCodes.Forbidden, result.ErrorCode);
        Assert.Null(result.ResourceUrl);
    }

    private static Mock<ILoanStore> StoreFor(Book book)
    {
        var store = new Mock<ILoanStore>();
        store.Setup(x => x.FindActiveUserAsync("ana")).ReturnsAsync(new User { Id = "u1", Username = "ana", IsActive = true });
        store.Setup(x => x.FindActiveBookAsync("b1", It.IsAny<CancellationToken>())).ReturnsAsync(book);
        store.Setup(x => x.HasActiveReservationAsync("u1", "b1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        store.Setup(x => x.InsertLoanAsync(It.IsAny<Loan>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return store;
    }

    private static Book Book(string mediaType) => new()
    {
        Id = "b1", Title = "Book", IsActive = true, MediaType = mediaType,
        TotalCopies = mediaType == MediaTypes.Physical ? 1 : null,
        AvailableCopies = mediaType == MediaTypes.Physical ? 1 : null
    };

    private static Loan ActiveLoan(string mediaType) => new()
    {
        Id = "l1", BookId = "b1", UserId = "u1", MediaType = mediaType,
        Status = LoanStatuses.Active, IsReturned = false
    };
}
