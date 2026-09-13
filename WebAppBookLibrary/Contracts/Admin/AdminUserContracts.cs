using System.ComponentModel.DataAnnotations;

namespace WebAppBookLibrary.Contracts.Admin;

public sealed record AdminUserResponse(string Id, string Username, string DisplayName, string Email, string Role, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastLoginAt);

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
