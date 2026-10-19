namespace WebAppBookLibrary.Domain.Support;

public static class SupportRules
{
    public static bool ValidCategory(string? category) => category is "account" or "loan" or "catalog" or "technical";
    public static bool ValidStatus(string? status) => status is "open" or "in_progress" or "resolved" or "closed";
    public static bool ValidText(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max;
    public static bool CanAttend(string role, string category) => ValidCategory(category) &&
        (role == "admin" || role == "librarian" && category is "loan" or "catalog");
    public static bool CanTransition(string current, string next, bool owner, bool staff, DateTime? resolvedAt, DateTime now, int reopenDays) =>
        owner && current == "resolved" && next == "open" && resolvedAt.HasValue && now >= resolvedAt && now <= resolvedAt.Value.AddDays(reopenDays) ||
        staff && (current, next) is ("open", "in_progress") or ("in_progress", "resolved") or ("resolved", "closed");
}
