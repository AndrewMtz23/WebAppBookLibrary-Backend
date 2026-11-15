namespace WebAppBookLibrary.Models;

public sealed class Organization
{
    public string Id { get; set; } = "initial";
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
