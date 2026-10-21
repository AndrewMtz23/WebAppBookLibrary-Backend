using WebAppBookLibrary.Models;

namespace WebAppBookLibrary.Domain.Copies;

public static class CopyRules
{
    public static string NormalizeCode(string code)
    {
        var value = code?.Trim() ?? string.Empty;
        if (value.Length is < 3 or > 64 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Inventory code must contain 3–64 ASCII letters, digits or hyphens.", nameof(code));
        return value.ToUpperInvariant();
    }

    public static bool ValidCondition(string? condition) => condition is "good" or "worn" or "damaged";
    public static bool ValidNotes(string? notes) => notes is null || notes.Length <= 1000;
    public static bool IsLendable(BookCopy copy) => copy.Status == "available" && !copy.InspectionPending
        && copy.Condition is "good" or "worn" && copy.ActiveAssignmentId is null;

    // Commitment resolution belongs to circulation, never the generic status editor.
    public static bool CanTransition(string from, string to, bool isAdmin) => (from, to) switch
    {
        ("available", "maintenance" or "lost") => true,
        ("maintenance", "available" or "lost") => true,
        ("lost", "maintenance") => true,
        ("available" or "maintenance" or "lost", "retired") => isAdmin,
        _ => false
    };
}
