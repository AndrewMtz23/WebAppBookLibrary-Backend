namespace WebAppBookLibrary.Contracts.Favorites;

public sealed record FavoriteResponse(string Id, string BookId, DateTime CreatedAt);
public sealed class FavoriteQuery
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public FavoriteQuery Normalize() => new() { Page = Math.Max(1, Page), PageSize = Math.Clamp(PageSize, 1, 100) };
}
