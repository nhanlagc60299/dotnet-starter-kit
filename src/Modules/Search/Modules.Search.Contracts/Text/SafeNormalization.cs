using System.Text;

namespace FSH.Modules.Search.Contracts.Text;

public static class SafeNormalization
{
    /// <summary>
    /// <see cref="string.Normalize(NormalizationForm)"/>, except that text .NET refuses to normalize (unpaired
    /// surrogates, noncharacters such as U+FFFE) is normalized without those characters instead of throwing.
    /// </summary>
    public static string SafeNormalize(this string text, NormalizationForm form)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return text.Normalize(form);
        }
        catch (ArgumentException)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i++];
                if (char.IsHighSurrogate(c) && i < text.Length && char.IsLowSurrogate(text[i]))
                {
                    sb.Append(c).Append(text[i++]);
                }
                else if (!char.IsSurrogate(c) && c is not ('\uFFFE' or '\uFFFF'))
                {
                    sb.Append(c);
                }
            }
            try
            {
                return sb.ToString().Normalize(form);
            }
            catch (ArgumentException)
            {
                return sb.ToString();
            }
        }
    }
}
