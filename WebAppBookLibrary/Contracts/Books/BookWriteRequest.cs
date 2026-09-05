using System.ComponentModel.DataAnnotations;
using WebAppBookLibrary.Domain.Books;

namespace WebAppBookLibrary.Contracts.Books;

public sealed record BookWriteRequest : IValidatableObject
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];
    public string? Isbn { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? Publisher { get; init; }
    public DateOnly? PublishedDate { get; init; }
    public string Language { get; init; } = "es";
    public int? PageCount { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Uri? CoverUrl { get; init; }
    public string MediaType { get; init; } = MediaTypes.Physical;
    public Uri? DigitalResourceUrl { get; init; }
    public int? TotalCopies { get; init; }
    public string? Author { get; init; }
    public int? Year { get; init; }
    public string? Genre { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var error in ValidateText(Title, nameof(Title), 1, 200)) yield return error;
        foreach (var error in ValidateOptionalText(Subtitle, nameof(Subtitle), 200)) yield return error;
        var legacy = Authors.Count == 0 && !string.IsNullOrWhiteSpace(Author);
        var effectiveAuthors = legacy ? new[] { Author! } : Authors;
        var effectiveGenres = legacy && Genres.Count == 0 && !string.IsNullOrWhiteSpace(Genre) ? new[] { Genre! } : Genres;
        var effectiveDescription = legacy && string.IsNullOrWhiteSpace(Description) ? "Sin descripción disponible para este registro heredado." : Description;
        foreach (var error in ValidateText(effectiveDescription, nameof(Description), 20, 5000)) yield return error;
        foreach (var error in ValidateOptionalText(Publisher, nameof(Publisher), 160)) yield return error;
        foreach (var error in ValidateList(effectiveAuthors, nameof(Authors), 1, 10)) yield return error;
        foreach (var error in ValidateList(effectiveGenres, nameof(Genres), 1, 8)) yield return error;
        foreach (var error in ValidateList(Tags, nameof(Tags), 0, 20)) yield return error;

        if (PageCount is < 1 or > 100000)
            yield return Invalid("Page count must be between 1 and 100000.", nameof(PageCount));

        if (string.IsNullOrWhiteSpace(Language) || Language.Trim().Length > 35)
            yield return Invalid("Language is required and must be a valid compact BCP 47 value.", nameof(Language));

        var isbn = BookRules.NormalizeIsbn(Isbn);
        if (isbn is not null && isbn.Length is not (10 or 13))
            yield return Invalid("ISBN must contain 10 or 13 characters after normalization.", nameof(Isbn));

        if (CoverUrl is not null && CoverUrl.Scheme != Uri.UriSchemeHttps)
            yield return Invalid("Cover URL must use HTTPS.", nameof(CoverUrl));

        var effectiveCopies = legacy && TotalCopies is null ? 1 : TotalCopies;
        foreach (var error in BookRules.ValidateMedia(MediaType, DigitalResourceUrl?.AbsoluteUri, effectiveCopies, effectiveCopies))
        {
            var member = error.Field switch
            {
                "digitalResourceUrl" => nameof(DigitalResourceUrl),
                "totalCopies" or "availableCopies" => nameof(TotalCopies),
                _ => nameof(MediaType)
            };
            yield return Invalid(error.Message, member);
        }
    }

    private static IEnumerable<ValidationResult> ValidateText(string? value, string member, int min, int max)
    {
        var length = value?.Trim().Length ?? 0;
        if (length < min || length > max)
            yield return Invalid($"{member} must contain between {min} and {max} characters.", member);
    }

    private static IEnumerable<ValidationResult> ValidateOptionalText(string? value, string member, int max)
    {
        if (value?.Trim().Length > max)
            yield return Invalid($"{member} cannot exceed {max} characters.", member);
    }

    private static IEnumerable<ValidationResult> ValidateList(IReadOnlyList<string> values, string member, int min, int max)
    {
        var normalized = BookRules.NormalizeList(values);
        if (normalized.Count < min || normalized.Count > max || normalized.Count != values.Count)
            yield return Invalid($"{member} must contain {min} to {max} unique non-empty values.", member);
    }

    private static ValidationResult Invalid(string message, string member) => new(message, [member]);
}
