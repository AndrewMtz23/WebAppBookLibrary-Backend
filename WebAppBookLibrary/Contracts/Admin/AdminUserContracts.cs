using System.ComponentModel.DataAnnotations;

namespace WebAppBookLibrary.Contracts.Admin;

public sealed record AdminUserResponse(string Id, string Username, string DisplayName, string Email, string? AvatarUrl, string Role, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastLoginAt);

public sealed class UpdateAdminUserRequest : IValidatableObject
{
    [Required, StringLength(100, MinimumLength = 3)]
    public string Username { get; init; } = string.Empty;
    [Required, StringLength(120, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;
    [Required, EmailAddress, StringLength(254)]
    public string Email { get; init; } = string.Empty;
    [StringLength(2048)]
    public string? AvatarUrl { get; init; }
    [Required]
    public string Role { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime ExpectedUpdatedAt { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ExpectedUpdatedAt == default)
            yield return new ValidationResult("ExpectedUpdatedAt is required.", [nameof(ExpectedUpdatedAt)]);
        if (!string.IsNullOrWhiteSpace(AvatarUrl) &&
            (!Uri.TryCreate(AvatarUrl.Trim(), UriKind.Absolute, out var uri) ||
             (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            yield return new ValidationResult("AvatarUrl must be an absolute HTTP or HTTPS URL.", [nameof(AvatarUrl)]);
    }
}

public sealed class AdminUserQuery : IValidatableObject
{
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase) { "createdAt", "lastLoginAt", "username", "role" };
    public const int MaxQueryLength = 200;
    public string? Query { get; set; }
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }
    public DateTime? LastLoginFrom { get; set; }
    public DateTime? LastLoginTo { get; set; }
    public string Sort { get; set; } = "username";
    public string Direction { get; set; } = "asc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public AdminUserQuery Normalize() => new() { Query = Bound(Query), Role = string.IsNullOrWhiteSpace(Role) ? null : Role.Trim().ToLowerInvariant(), IsActive = IsActive, CreatedFrom = Utc(CreatedFrom), CreatedTo = Utc(CreatedTo), LastLoginFrom = Utc(LastLoginFrom), LastLoginTo = Utc(LastLoginTo), Sort = AllowedSorts.Contains(Sort) ? Sort : "username", Direction = string.Equals(Direction, "desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc", Page = Math.Max(1, Page), PageSize = Math.Clamp(PageSize, 1, 100) };
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (CreatedFrom > CreatedTo) yield return new("createdFrom must be before createdTo.", [nameof(CreatedTo)]);
        if (LastLoginFrom > LastLoginTo) yield return new("lastLoginFrom must be before lastLoginTo.", [nameof(LastLoginTo)]);
    }
    private static string? Bound(string? value) { var text = value?.Trim(); return string.IsNullOrEmpty(text) ? null : text[..Math.Min(text.Length, MaxQueryLength)]; }
    private static DateTime? Utc(DateTime? value) => value?.ToUniversalTime();
}

public sealed record SetUserRoleRequest([Required] string Role);
public sealed record SetUserStatusRequest(bool IsActive);
