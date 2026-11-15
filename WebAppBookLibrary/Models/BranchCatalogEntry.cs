namespace WebAppBookLibrary.Models;

public sealed class BranchCatalogEntry
{
    public string Id { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public long Version { get; set; } = 1;
    public DateTime UpdatedAt { get; set; }
}
