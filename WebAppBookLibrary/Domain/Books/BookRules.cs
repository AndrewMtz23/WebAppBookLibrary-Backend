using System.Text.RegularExpressions;
using WebAppBookLibrary.Domain.Common;

namespace WebAppBookLibrary.Domain.Books;

public static partial class BookRules
{
    public const int CurrentSchemaVersion = 2;

    public static string? NormalizeIsbn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = IsbnSeparators().Replace(value, string.Empty).ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    public static IReadOnlyList<string> NormalizeList(IEnumerable<string>? values)
    {
        if (values is null)
            return [];

        return values
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeLanguage(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "es"
            : value.Trim().ToLowerInvariant();
    }

    public static IReadOnlyList<DomainValidationError> ValidateMedia(
        string? mediaType,
        string? digitalResourceUrl,
        int? totalCopies,
        int? availableCopies,
        bool isActive = true)
    {
        var errors = new List<DomainValidationError>();

        if (!MediaTypes.IsCanonical(mediaType))
        {
            errors.Add(new("media_type_invalid", "mediaType", "Media type must be physical or digital."));
            return errors;
        }

        if (mediaType == MediaTypes.Digital)
        {
            if (totalCopies is not null || availableCopies is not null)
                errors.Add(new("digital_inventory_not_allowed", "totalCopies", "Digital books cannot define physical inventory."));

            if (isActive && !IsAbsoluteHttps(digitalResourceUrl))
                errors.Add(new("digital_resource_invalid", "digitalResourceUrl", "An active digital book requires an absolute HTTPS resource URL."));

            return errors;
        }

        if (digitalResourceUrl is not null)
            errors.Add(new("physical_resource_not_allowed", "digitalResourceUrl", "Physical books cannot define a digital resource URL."));

        if (totalCopies is null || availableCopies is null ||
            totalCopies < 0 || availableCopies < 0 || availableCopies > totalCopies)
        {
            errors.Add(new("physical_inventory_invalid", "availableCopies", "Physical inventory must satisfy 0 <= availableCopies <= totalCopies."));
        }

        return errors;
    }

    public static bool IsAbsoluteHttps(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    [GeneratedRegex("[-\\s]")]
    private static partial Regex IsbnSeparators();
}
