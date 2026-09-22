namespace WebAppBookLibrary.Contracts.Books;

public sealed record BookSummaryResponse(string Id, string Title, string? Subtitle, IReadOnlyList<string> Authors, string? CoverUrl, string MediaType, IReadOnlyList<string> Genres, int? AvailableCopies, int? TotalCopies, long ReservationCount, bool IsFavorite, bool IsActive)
{
    public IReadOnlyList<string> CategoryIds { get; init; } = [];
    public IReadOnlyList<WebAppBookLibrary.Contracts.Categories.BookCategoryResponse> Categories { get; init; } = [];
}
