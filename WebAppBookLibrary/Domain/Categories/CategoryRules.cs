using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
namespace WebAppBookLibrary.Domain.Categories;
public static class CategoryRules
{
    public static string DisplayName(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");
    public static string NormalizeName(string value)
    {
        // Normalize composed/decomposed enye first and keep it distinct from n.
        var result = new StringBuilder();
        foreach (var character in DisplayName(value).ToLowerInvariant().EnumerateRunes())
        {
            if (character.Value == '\u00f1') { result.Append(character.ToString()); continue; }
            foreach (var part in character.ToString().Normalize(NormalizationForm.FormD).EnumerateRunes())
                if (Rune.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark) result.Append(part.ToString());
        }
        return result.ToString().Normalize(NormalizationForm.FormC);
    }
    public static string CreateSlug(string name, string? suffix = null)
    {
        var slug = Regex.Replace(NormalizeName(name), @"[^\p{L}\p{N}]+", "-").Trim('-');
        if (slug.Length == 0) slug = "categoria";
        return suffix is null ? slug : $"{slug}-{suffix}";
    }
}
