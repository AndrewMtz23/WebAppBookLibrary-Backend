using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Domain.Books;

namespace WebAppBookLibrary.Tests;

public sealed class BookMapperTests
{
    [Fact]
    public void ToNewEntity_NormalizesMetadataAndOwnsDerivedFields()
    {
        var now = new DateTime(2026, 9, 4, 15, 0, 0, DateTimeKind.Utc);
        var request = new BookWriteRequest
        {
            Title = "  The Left Hand of Darkness ",
            Authors = [" Ursula K. Le Guin ", "ursula k. le guin"],
            Isbn = "978-0-441-47812-5",
            Description = "  A landmark novel about culture, identity, and human connection.  ",
            Language = " EN ",
            Genres = [" Science Fiction "],
            Tags = [" Classic ", "classic"],
            MediaType = MediaTypes.Physical,
            TotalCopies = 4
        };

        var book = BookMapper.ToNewEntity(request, "507f1f77bcf86cd799439011", now);

        Assert.Equal("The Left Hand of Darkness", book.Title);
        Assert.Equal(["Ursula K. Le Guin"], book.Authors);
        Assert.Equal("9780441478125", book.Isbn);
        Assert.Equal("en", book.Language);
        Assert.Equal(4, book.TotalCopies);
        Assert.Equal(4, book.AvailableCopies);
        Assert.Equal(now, book.CreatedAt);
        Assert.Equal(now, book.UpdatedAt);
        Assert.True(book.IsActive);
    }

    [Fact]
    public void ToNewEntity_DigitalBookNeverReceivesInventory()
    {
        var request = new BookWriteRequest
        {
            Title = "Digital title",
            Authors = ["Author"],
            Description = "A sufficiently descriptive digital book description.",
            Genres = ["Essay"],
            MediaType = MediaTypes.Digital,
            DigitalResourceUrl = new Uri("https://cdn.example/book.pdf")
        };

        var book = BookMapper.ToNewEntity(request, "507f1f77bcf86cd799439011", DateTime.UtcNow);

        Assert.Null(book.TotalCopies);
        Assert.Null(book.AvailableCopies);
        Assert.Null(book.ActiveLoanId);
    }
}
