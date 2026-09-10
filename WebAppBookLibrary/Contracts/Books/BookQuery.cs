using WebAppBookLibrary.Domain.Books;

namespace WebAppBookLibrary.Contracts.Books;

public sealed class BookQuery
{
    public const int MaxQueryLength = 200;
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase) { "createdAt", "title", "publishedDate", "reservationCount", "relevance" };
    public string? Query { get; init; }
    public string? Genre { get; init; }
    public string? MediaType { get; init; }
    public string? Language { get; init; }
    public bool? Available { get; init; }
    public bool? IsActive { get; set; }
    public bool? LowStock { get; set; }
    public bool? MissingResource { get; set; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public string Sort { get; init; } = "createdAt";
    public string Direction { get; init; } = "desc";

    public NormalizedBookQuery Normalize()
    {
        var normalizedQuery = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim()[..Math.Min(Query.Trim().Length, MaxQueryLength)];
        var normalizedSort = AllowedSorts.Contains(Sort) ? Sort : "createdAt";
        if (normalizedSort.Equals("relevance", StringComparison.OrdinalIgnoreCase) && normalizedQuery is null) normalizedSort = "createdAt";
        return new(
        normalizedQuery,
        string.IsNullOrWhiteSpace(Genre) ? null : Genre.Trim(),
        MediaTypes.IsCanonical(MediaType) ? MediaType : null,
        string.IsNullOrWhiteSpace(Language) ? null : BookRules.NormalizeLanguage(Language),
        Available, IsActive, LowStock, MissingResource,
        Math.Max(1, Page),
        Math.Clamp(PageSize, 1, 100),
        normalizedSort,
        string.Equals(Direction, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc",
        IncludeIdTieBreaker: true);
    }
}

public sealed record NormalizedBookQuery(string? Query, string? Genre, string? MediaType, string? Language, bool? Available, bool? IsActive, bool? LowStock, bool? MissingResource, int Page, int PageSize, string Sort, string Direction, bool IncludeIdTieBreaker);
