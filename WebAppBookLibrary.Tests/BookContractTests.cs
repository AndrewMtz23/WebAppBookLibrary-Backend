using System.ComponentModel.DataAnnotations;
using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Domain.Books;

namespace WebAppBookLibrary.Tests;

public sealed class BookContractTests
{
    [Fact]
    public void BookWriteRequest_RejectsDigitalInventoryAndInsecureUrls()
    {
        var request = ValidDigitalRequest() with
        {
            CoverUrl = new Uri("http://cdn.example/cover.jpg"),
            DigitalResourceUrl = new Uri("http://cdn.example/book.pdf"),
            TotalCopies = 1
        };

        var errors = Validate(request);

        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(request.CoverUrl)));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(request.DigitalResourceUrl)));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(request.TotalCopies)));
    }

    [Fact]
    public void BookWriteRequest_RejectsDuplicateNormalizedAuthorsAndGenres()
    {
        var request = ValidDigitalRequest() with
        {
            Authors = ["Octavia Butler", " octavia butler "],
            Genres = ["Ciencia ficción", " ciencia ficción "]
        };

        var errors = Validate(request);

        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(request.Authors)));
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(request.Genres)));
    }

    [Fact]
    public void BookQuery_NormalizeClampsPaginationAndFallsBackToStableSort()
    {
        var normalized = new BookQuery { Page = 0, PageSize = 500, Sort = "unsupported", Direction = "sideways" }.Normalize();

        Assert.Equal(1, normalized.Page);
        Assert.Equal(100, normalized.PageSize);
        Assert.Equal("createdAt", normalized.Sort);
        Assert.Equal("desc", normalized.Direction);
        Assert.True(normalized.IncludeIdTieBreaker);
    }

    [Fact]
    public void BookWriteRequest_AcceptsTheLegacyAuthorYearGenreShapeDuringTransition()
    {
        var request = new BookWriteRequest { Title = "Legacy title", Author = "Legacy author", Year = 1998, Genre = "Novel" };

        Assert.Empty(Validate(request));
    }

    private static BookWriteRequest ValidDigitalRequest() => new()
    {
        Title = "Parable of the Sower",
        Authors = ["Octavia E. Butler"],
        Description = "A powerful novel about survival, community, and change.",
        Language = "en",
        Genres = ["Science fiction"],
        MediaType = MediaTypes.Digital,
        DigitalResourceUrl = new Uri("https://cdn.example/book.pdf")
    };

    private static IReadOnlyList<ValidationResult> Validate(BookWriteRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }
}
