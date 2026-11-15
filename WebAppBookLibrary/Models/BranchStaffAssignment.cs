namespace WebAppBookLibrary.Models;

public sealed class BranchStaffAssignment
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public long Version { get; set; } = 1;
    public long ReferenceVersion { get; set; }
}
