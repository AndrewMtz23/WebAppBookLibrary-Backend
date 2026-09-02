using System.ComponentModel.DataAnnotations;

namespace WebAppBookLibrary.Contracts.Books;

public sealed class UpsertBookRequest
{
    [Required]
    [StringLength(200)]
    public string Title { get; init; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Author { get; init; } = string.Empty;

    [Range(1000, 2100)]
    public int? Year { get; init; }

    [StringLength(100)]
    public string Genre { get; init; } = string.Empty;
}
