namespace WebAppBookLibrary.Security;

public static class RoleNames
{
    public const string User = "user";
    public const string Librarian = "librarian";
    public const string Admin = "admin";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is User or Librarian or Admin;
    }
}
