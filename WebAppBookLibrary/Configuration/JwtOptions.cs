using System.ComponentModel.DataAnnotations;

namespace WebAppBookLibrary.Configuration;

public sealed class JwtOptions : IValidatableObject
{
    private static readonly string[] PlaceholderMarkers =
    [
        "placeholder",
        "change-me",
        "change-this-in-production",
        "change-in-production",
        "default-development",
        "minimum-32-characters",
        "at-least-32-characters",
        "replace-with"
    ];

    public const string SectionName = "Jwt";

    [Required]
    [MinLength(32)]
    public string Key { get; init; } = string.Empty;

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var normalizedKey = Key.Trim().ToLowerInvariant();
        var containsInstruction = PlaceholderMarkers.Any(marker =>
            normalizedKey.Contains(marker, StringComparison.Ordinal));
        var hasExamplePrefix =
            (normalizedKey.StartsWith("your-", StringComparison.Ordinal) ||
             normalizedKey.StartsWith("example-", StringComparison.Ordinal) ||
             normalizedKey.StartsWith("sample-", StringComparison.Ordinal)) &&
            (normalizedKey.Contains("key", StringComparison.Ordinal) ||
             normalizedKey.Contains("secret", StringComparison.Ordinal));

        if (containsInstruction || hasExamplePrefix)
        {
            yield return new ValidationResult(
                "JWT signing key must not be a placeholder value.",
                [nameof(Key)]);
        }
    }
}
