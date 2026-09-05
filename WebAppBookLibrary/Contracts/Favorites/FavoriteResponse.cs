namespace WebAppBookLibrary.Contracts.Favorites;

public sealed record FavoriteResponse(string Id, string BookId, DateTime CreatedAt);
