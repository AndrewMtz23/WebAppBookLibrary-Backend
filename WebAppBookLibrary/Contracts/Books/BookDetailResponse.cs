namespace WebAppBookLibrary.Contracts.Books;

public sealed record BookDetailResponse(string Id, string Title, string? Subtitle, IReadOnlyList<string> Authors, string? Isbn, string Description, string? Publisher, DateTime? PublishedDate, string Language, int? PageCount, IReadOnlyList<string> Genres, IReadOnlyList<string> Tags, string? CoverUrl, string MediaType, string? DigitalResourceUrl, int? AvailableCopies, int? TotalCopies, long ReservationCount, bool IsFavorite, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);
