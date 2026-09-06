namespace WebAppBookLibrary.Contracts.Books;

public sealed record BookSummaryResponse(string Id, string Title, string? Subtitle, IReadOnlyList<string> Authors, string? CoverUrl, string MediaType, IReadOnlyList<string> Genres, int? AvailableCopies, int? TotalCopies, long ReservationCount, bool IsFavorite, bool IsActive);
