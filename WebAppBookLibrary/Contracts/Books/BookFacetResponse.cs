namespace WebAppBookLibrary.Contracts.Books;

public sealed record BookFacetResponse(string Value, long Count)
{
    public string? Id { get; init; }
    public string Name { get; init; } = Value;
    public string? Slug { get; init; }
}
