using System.Globalization;
using System.Text;

namespace FSH.Modules.Search.Contracts.Text;

/// <summary>Lowercase, unaccented text for diacritic-insensitive matching ("Học" and "hoc" are equal).</summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return StripMarks(text).Normalize(NormalizationForm.FormC);
    }

    /// <summary>Lowercases, folds đ to d, decomposes and drops the combining marks (the result is FormD).</summary>
    internal static string StripMarks(string text)
    {
#pragma warning disable CA1308 // search tokens and slugs are lowercase by definition
        string decomposed = text.ToLowerInvariant()
            .Replace('đ', 'd')
            .SafeNormalize(NormalizationForm.FormD);
#pragma warning restore CA1308

        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString();
    }
}
