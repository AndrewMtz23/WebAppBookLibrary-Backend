using System.Text.RegularExpressions;

namespace WebAppBookLibrary.Domain.Branches;

public static partial class BranchRules
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static string NormalizeCode(string? code)
    {
        var value = code?.Trim().ToUpperInvariant() ?? "";
        return CodePattern().IsMatch(value) ? value : throw new ArgumentException("Invalid branch code.");
    }

    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 120;
    public static bool ValidTimeZone(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Length > 100) return false;
        if (zone != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(zone, out _)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(zone); return true; }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }
}
