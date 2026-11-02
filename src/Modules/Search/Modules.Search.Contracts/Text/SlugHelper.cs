using System.Text.RegularExpressions;

namespace FSH.Modules.Search.Contracts.Text;

public static partial class SlugHelper
{
    /// <summary>
    /// Converts text (including Vietnamese with diacritics) into a URL-friendly slug:
    /// "Xin chào Việt Nam!" becomes "xin-chao-viet-nam".
    /// </summary>
    public static string Generate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return NonAlphanumeric().Replace(TextNormalizer.StripMarks(text.Trim()), "-").Trim('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
