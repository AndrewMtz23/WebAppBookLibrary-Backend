using WebAppBookLibrary.Contracts.Books;
using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Domain.Books;

public static class BookMapper
{
    public static Book ToNewEntity(BookWriteRequest request, string id, DateTime nowUtc)
    {
        nowUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        var legacy = request.Authors.Count == 0 && !string.IsNullOrWhiteSpace(request.Author);
        var authors = BookRules.NormalizeList(legacy ? [request.Author!] : request.Authors).ToList();
        var genres = BookRules.NormalizeList(request.Genres.Count == 0 && !string.IsNullOrWhiteSpace(request.Genre) ? [request.Genre!] : request.Genres).ToList();
        var totalCopies = request.MediaType == MediaTypes.Physical ? request.TotalCopies ?? (legacy ? 1 : null) : null;
        var publishedDate = request.PublishedDate ?? (request.Year is null ? null : new DateOnly(request.Year.Value, 1, 1));
        return new Book
        {
            Id = id,
            Title = request.Title.Trim(),
            Subtitle = NullIfWhiteSpace(request.Subtitle),
            Authors = authors,
            Isbn = BookRules.NormalizeIsbn(request.Isbn),
            Description = string.IsNullOrWhiteSpace(request.Description) && legacy ? "Sin descripción disponible para este registro heredado." : request.Description.Trim(),
            Publisher = NullIfWhiteSpace(request.Publisher),
            PublishedDate = publishedDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
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
            Year = publishedDate?.Year,
            Genre = genres.FirstOrDefault() ?? string.Empty,
            IsAvailable = request.MediaType == MediaTypes.Digital || totalCopies > 0,
            ActiveLoanId = null
        };
    }

    public static BookSummaryResponse ToSummary(Book book, long reservationCount, bool isFavorite) =>
        new(book.Id, book.Title, book.Subtitle, EffectiveAuthors(book), book.CoverUrl, EffectiveMediaType(book), EffectiveGenres(book), EffectiveAvailable(book), EffectiveTotal(book), reservationCount, isFavorite, book.IsActive);

    public static BookDetailResponse ToDetail(Book book, long reservationCount, bool isFavorite) =>
        new(book.Id, book.Title, book.Subtitle, EffectiveAuthors(book), book.Isbn, string.IsNullOrWhiteSpace(book.Description) ? "Sin descripción disponible para este registro heredado." : book.Description, book.Publisher, book.PublishedDate ?? (book.Year is null ? null : new DateTime(book.Year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc)), book.Language, book.PageCount, EffectiveGenres(book), book.Tags, book.CoverUrl, EffectiveMediaType(book), EffectiveAvailable(book), EffectiveTotal(book), reservationCount, isFavorite, book.IsActive, book.CreatedAt, book.UpdatedAt);

    public static Book ToUpdatedEntity(BookWriteRequest request, Book existing, int activePhysicalLoans, DateTime nowUtc)
    {
        var updated = ToNewEntity(request, existing.Id, nowUtc);
        updated.CreatedAt = existing.CreatedAt;
        updated.ReferenceVersion = existing.ReferenceVersion;
        updated.IsActive = existing.IsActive;
        updated.AvailableCopies = updated.MediaType == MediaTypes.Physical
            ? updated.TotalCopies - activePhysicalLoans
            : null;
        updated.IsAvailable = updated.MediaType == MediaTypes.Digital || updated.AvailableCopies > 0;
        updated.ActiveLoanId = existing.ActiveLoanId;
        return updated;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IReadOnlyList<string> EffectiveAuthors(Book book) => book.Authors.Count > 0 ? book.Authors : string.IsNullOrWhiteSpace(book.Author) ? [] : [book.Author];
    private static IReadOnlyList<string> EffectiveGenres(Book book) => book.Genres.Count > 0 ? book.Genres : string.IsNullOrWhiteSpace(book.Genre) ? [] : [book.Genre];
    private static string EffectiveMediaType(Book book) => string.IsNullOrWhiteSpace(book.MediaType) ? MediaTypes.Physical : book.MediaType;
    private static int? EffectiveTotal(Book book) => EffectiveMediaType(book) == MediaTypes.Physical ? book.TotalCopies ?? 1 : null;
    private static int? EffectiveAvailable(Book book) => EffectiveMediaType(book) == MediaTypes.Physical ? book.AvailableCopies ?? (book.IsAvailable ? 1 : 0) : null;
}
