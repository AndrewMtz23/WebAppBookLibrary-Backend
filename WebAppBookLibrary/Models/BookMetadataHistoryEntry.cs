namespace WebAppBookLibrary.Models;

public sealed class BookMetadataHistoryEntry
{
    public Book? Snapshot { get; set; }
    public string Id { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string? ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public long Version { get; set; }
    public string? RequestId { get; set; }
    public DateTime CreatedAt { get; set; }
}
