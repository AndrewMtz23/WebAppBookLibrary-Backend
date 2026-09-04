using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Domain.Books;

public static class BookMapper
{
    public static Book ToNewEntity(BookWriteRequest request, string id, DateTime nowUtc)
    {
        var authors = BookRules.NormalizeList(request.Authors).ToList();
        var genres = BookRules.NormalizeList(request.Genres).ToList();
        var totalCopies = request.MediaType == MediaTypes.Physical ? request.TotalCopies : null;
        return new Book
        {
            Id = id,
            Title = request.Title.Trim(),
            Subtitle = NullIfWhiteSpace(request.Subtitle),
            Authors = authors,
            Isbn = BookRules.NormalizeIsbn(request.Isbn),
            Description = request.Description.Trim(),
            Publisher = NullIfWhiteSpace(request.Publisher),
            PublishedDate = request.PublishedDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            Language = BookRules.NormalizeLanguage(request.Language),
            PageCount = request.PageCount,
            Genres = genres,
            Tags = BookRules.NormalizeList(request.Tags).ToList(),
            CoverUrl = request.CoverUrl?.AbsoluteUri,
            MediaType = request.MediaType,
            DigitalResourceUrl = request.MediaType == MediaTypes.Digital ? request.DigitalResourceUrl?.AbsoluteUri : null,
            TotalCopies = totalCopies,
            AvailableCopies = totalCopies,
            IsActive = true,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc,
            SchemaVersion = BookRules.CurrentSchemaVersion,
            Author = authors.FirstOrDefault() ?? string.Empty,
            Year = request.PublishedDate?.Year,
            Genre = genres.FirstOrDefault() ?? string.Empty,
            IsAvailable = request.MediaType == MediaTypes.Digital || totalCopies > 0,
            ActiveLoanId = null
        };
    }

    public static BookSummaryResponse ToSummary(Book book, long reservationCount, bool isFavorite) =>
        new(book.Id, book.Title, book.Subtitle, book.Authors, book.CoverUrl, book.MediaType, book.Genres, book.AvailableCopies, book.TotalCopies, reservationCount, isFavorite, book.IsActive);

    public static BookDetailResponse ToDetail(Book book, long reservationCount, bool isFavorite) =>
        new(book.Id, book.Title, book.Subtitle, book.Authors, book.Isbn, book.Description, book.Publisher, book.PublishedDate, book.Language, book.PageCount, book.Genres, book.Tags, book.CoverUrl, book.MediaType, book.DigitalResourceUrl, book.AvailableCopies, book.TotalCopies, reservationCount, isFavorite, book.IsActive, book.CreatedAt, book.UpdatedAt);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
