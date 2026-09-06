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

    public BookWriteRequest ToBookWriteRequest() => new()
    {
        Title = Title,
        Authors = [Author],
        Description = "Sin descripción disponible para este registro heredado.",
        PublishedDate = Year is null ? null : new DateOnly(Year.Value, 1, 1),
        Genres = string.IsNullOrWhiteSpace(Genre) ? ["Sin clasificar"] : [Genre],
        MediaType = Domain.Books.MediaTypes.Physical,
        TotalCopies = 1
    };
}
