using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using WebAppBookLibrary.Domain.Categories;
namespace WebAppBookLibrary.Contracts.Categories;
public sealed record CategoryResponse(string Id, string Name, string Slug, string? Description, bool IsActive, long Version, DateTime CreatedAt, DateTime UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? BookCount = null);
public sealed record BookCategoryResponse(string Id, string Name, string Slug, bool IsActive);
public sealed class CategoryQuery
{
    [StringLength(200)] public string? Query { get; init; }
    public bool? IsActive { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
}
public sealed record CategoryWriteRequest : IValidatableObject
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public long Version { get; init; }
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Name is null || CategoryRules.DisplayName(Name).Length is < 1 or > 80 || Name.Any(char.IsControl))
            yield return new("El nombre debe contener entre 1 y 80 caracteres.", [nameof(Name)]);
        if (Description?.Trim().Length > 500) yield return new("La descripci\u00f3n admite hasta 500 caracteres.", [nameof(Description)]);
    }
}
public sealed record CategoryStatusRequest(bool IsActive, [Range(typeof(long), "1", "9223372036854775807")] long Version);
