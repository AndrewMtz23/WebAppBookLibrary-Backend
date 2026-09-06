using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Domain.Books;
using WebAppBookLibrary.Domain.Common;
using WebAppBookLibrary.Models;
using WebAppBookLibrary.Services;

namespace WebAppBookLibrary.Tests;

public sealed class BookServiceTests
{
    [Fact]
    public async Task SearchAsync_ReturnsMappedPaginationAndContextualFields()
    {
        var book = new Book
        {
            Id = "507f1f77bcf86cd799439011",
            Title = "Kindred",
            Authors = ["Octavia E. Butler"],
            Description = "A novel about history, power, memory, and survival.",
            Genres = ["Science fiction"],
            MediaType = MediaTypes.Physical,
            TotalCopies = 3,
            AvailableCopies = 1,
            IsActive = true
        };
        var store = new StubBookStore(new PagedResult<BookCatalogEntry>(
            [new BookCatalogEntry(book, ReservationCount: 14, IsFavorite: true)], 2, 20, 21));
        var service = new BookService(store);

        var result = await service.SearchAsync(new BookQuery { Page = 2 }, includeInactive: false, CancellationToken.None);

        Assert.Equal(2, result.Page);
        Assert.Equal(2, result.TotalPages);
        var item = Assert.Single(result.Items);
        Assert.Equal("Kindred", item.Title);
        Assert.Equal(14, item.ReservationCount);
        Assert.True(item.IsFavorite);
    }

    [Fact]
    public async Task SetActiveAsync_ReturnsNotFoundWhenNoBookMatches()
    {
        var store = new StubBookStore(new PagedResult<BookCatalogEntry>([], 1, 20, 0)) { StatusResult = false };
        var service = new BookService(store);

        var result = await service.SetActiveAsync("507f1f77bcf86cd799439011", false, new DateTime(2026, 9, 4, 16, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("book_not_found", result.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_RejectsAnExistingNormalizedIsbn()
    {
        var store = new StubBookStore(new PagedResult<BookCatalogEntry>([], 1, 20, 0)) { IsbnExists = true };
        var service = new BookService(store);
        var request = new BookWriteRequest
        {
            Title = "A complete title",
            Authors = ["Author"],
            Isbn = "978-0-306-40615-7",
            Description = "A complete description with enough useful information.",
            Genres = ["Essay"],
            MediaType = MediaTypes.Physical,
            TotalCopies = 2
        };

        var result = await service.CreateAsync(request, "507f1f77bcf86cd799439011", DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("isbn_conflict", result.ErrorCode);
        Assert.Null(result.Book);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsProjectionWithCatalogContext()
    {
        var book = CreatePhysicalBook();
        var store = new StubBookStore(new PagedResult<BookCatalogEntry>([], 1, 20, 0))
        {
            DetailEntry = new BookCatalogEntry(book, 7, true)
        };
        var service = new BookService(store);

        var result = await service.GetDetailAsync(book.Id, includeInactive: false, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7, result.ReservationCount);
        Assert.True(result.IsFavorite);
        Assert.Equal(book.Isbn, result.Isbn);
    }

    [Fact]
    public async Task UpdateAsync_PreservesCreatedAtAndActivePhysicalInventory()
    {
        var existing = CreatePhysicalBook();
        existing.TotalCopies = 5;
        existing.AvailableCopies = 2;
        var store = new StubBookStore(new PagedResult<BookCatalogEntry>([], 1, 20, 0))
        {
            FoundBook = existing,
            ActivePhysicalLoans = 3
        };
        var service = new BookService(store);
        var request = new BookWriteRequest
        {
            Title = "Updated title",
            Authors = ["Updated author"],
            Description = "An updated description with enough meaningful detail.",
            Genres = ["Essay"],
            MediaType = MediaTypes.Physical,
            TotalCopies = 6
        };
        var updatedAt = existing.UpdatedAt.AddDays(1);

        var result = await service.UpdateAsync(existing.Id, request, updatedAt, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(store.ReplacedBook);
        Assert.Equal(existing.CreatedAt, store.ReplacedBook.CreatedAt);
        Assert.Equal(3, store.ReplacedBook.AvailableCopies);
        Assert.Equal(updatedAt, store.ReplacedBook.UpdatedAt);
    }

    [Fact]
    public async Task UpdateAsync_RejectsTotalCopiesBelowActivePhysicalLoans()
    {
        var existing = CreatePhysicalBook();
        var store = new StubBookStore(new PagedResult<BookCatalogEntry>([], 1, 20, 0))
        {
            FoundBook = existing,
            ActivePhysicalLoans = 2
        };
        var service = new BookService(store);
        var request = new BookWriteRequest
        {
            Title = "Updated title",
            Authors = ["Updated author"],
            Description = "An updated description with enough meaningful detail.",
            Genres = ["Essay"],
            MediaType = MediaTypes.Physical,
            TotalCopies = 1
        };

        var result = await service.UpdateAsync(existing.Id, request, DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("inventory_conflict", result.ErrorCode);
        Assert.Null(store.ReplacedBook);
    }

    [Fact]
    public async Task UpdateAsync_RejectsMediaChangeWhilePhysicalLoansAreActive()
    {
        var existing = CreatePhysicalBook();
        var store = new StubBookStore(new PagedResult<BookCatalogEntry>([], 1, 20, 0)) { FoundBook = existing, ActivePhysicalLoans = 1 };
        var service = new BookService(store);
        var request = new BookWriteRequest
        {
            Title = "Digital replacement", Authors = ["Author"],
            Description = "A sufficiently descriptive digital replacement book.", Genres = ["Essay"],
            MediaType = MediaTypes.Digital, DigitalResourceUrl = new Uri("https://cdn.example/book.pdf")
        };

        var result = await service.UpdateAsync(existing.Id, request, DateTime.UtcNow, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("inventory_conflict", result.ErrorCode);
        Assert.Null(store.ReplacedBook);
    }

    private static Book CreatePhysicalBook() => new()
    {
        Id = "507f1f77bcf86cd799439011",
        Title = "Original title",
        Authors = ["Original author"],
        Isbn = "9780306406157",
        Description = "An original description with enough meaningful detail.",
        Genres = ["Novel"],
        MediaType = MediaTypes.Physical,
        TotalCopies = 5,
        AvailableCopies = 2,
        IsActive = true,
        CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
    };

    private sealed class StubBookStore(PagedResult<BookCatalogEntry> result) : IBookStore
    {
        public bool StatusResult { get; init; } = true;
        public bool IsbnExists { get; init; }
        public BookCatalogEntry? DetailEntry { get; init; }
        public Book? FoundBook { get; init; }
        public int ActivePhysicalLoans { get; init; }
        public Book? ReplacedBook { get; private set; }
        public bool ReplaceResult { get; init; } = true;
        public Task<PagedResult<BookCatalogEntry>> SearchAsync(NormalizedBookQuery query, bool includeInactive, string? viewerUsername, CancellationToken cancellationToken) => Task.FromResult(result);
        public Task<BookCatalogEntry?> FindCatalogEntryAsync(string id, bool includeInactive, string? viewerUsername, CancellationToken cancellationToken) => Task.FromResult(DetailEntry);
        public Task<Book?> FindByIdAsync(string id, CancellationToken cancellationToken) => Task.FromResult(FoundBook);
        public Task<bool> IsbnExistsAsync(string normalizedIsbn, string? excludingId, CancellationToken cancellationToken) => Task.FromResult(IsbnExists);
        public Task InsertAsync(Book book, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<int> CountActivePhysicalLoansAsync(string bookId, CancellationToken cancellationToken) => Task.FromResult(ActivePhysicalLoans);
        public Task<bool> ReplaceMetadataAsync(Book book, DateTime expectedUpdatedAt, CancellationToken cancellationToken) { ReplacedBook = book; return Task.FromResult(ReplaceResult); }
        public Task<bool> SetActiveAsync(string id, bool isActive, DateTime updatedAtUtc, CancellationToken cancellationToken) => Task.FromResult(StatusResult);
    }
}
