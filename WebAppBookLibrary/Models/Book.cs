using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using WebAppBookLibrary.Domain.Books;

namespace WebAppBookLibrary.Models;

public class Book
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public List<string> Authors { get; set; } = [];
    public string? Isbn { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public DateTime? PublishedDate { get; set; }
    public string Language { get; set; } = "es";
    public int? PageCount { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string? CoverUrl { get; set; }
    public string MediaType { get; set; } = MediaTypes.Physical;
    public string? DigitalResourceUrl { get; set; }
    public int? TotalCopies { get; set; }
    public int? AvailableCopies { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int SchemaVersion { get; set; } = BookRules.CurrentSchemaVersion;

    // Legacy fields remain readable until the explicit schema migration is applied.
    public string Author { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string Genre { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;

    [BsonRepresentation(BsonType.ObjectId)]
    [BsonIgnoreIfNull]
    [JsonIgnore]
    public string? ActiveLoanId { get; set; }
}
