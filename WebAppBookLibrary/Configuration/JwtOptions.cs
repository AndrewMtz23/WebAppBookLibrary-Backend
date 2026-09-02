using System.ComponentModel.DataAnnotations;

namespace WebAppBookLibrary.Configuration;

public sealed class JwtOptions : IValidatableObject
{
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
        if (normalizedKey.Contains("placeholder", StringComparison.Ordinal) ||
            normalizedKey.Contains("change-in-production", StringComparison.Ordinal) ||
            normalizedKey.Contains("change-me", StringComparison.Ordinal) ||
            normalizedKey.Contains("default-development", StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "JWT signing key must not be a placeholder value.",
                [nameof(Key)]);
        }
    }
}
