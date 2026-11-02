using System.Text;
using FSH.Modules.Search.Contracts.Text;

namespace FSH.Modules.Search.Features.v1.Documents.SearchDocuments;

/// <summary>One term of a search: a token, or a quoted phrase of tokens; optionally negated (<c>-word</c>) or a prefix.</summary>
public sealed record SearchTerm(IReadOnlyList<string> Tokens, bool Negated = false, bool Prefix = false)
{
    public bool IsPhrase => Tokens.Count > 1;
}

/// <summary>
/// A parsed search string: web-search syntax (<c>"exact phrase"</c>, <c>-exclude</c>, <c>or</c>) over Vietnamese
/// syllables. Every token is reduced to <c>[a-z0-9]</c> after <see cref="TextNormalizer"/> (lowercase, unaccented,
/// đ → d), so no tsquery syntax can be injected. The last bare token is a prefix (autocomplete) unless the text ends
/// with a space. <see cref="PhraseTokens"/> (two or more bare tokens, no operators) is the phrase whose match boosts
/// ranking, which orders "học sinh" (student) before "sinh học" (biology).
/// </summary>
public sealed class SearchText
{
    public const int MaxLength = 200;
    public const int MaxTokens = 32;
    public const int MinPrefixLength = 2;

    private SearchText(IReadOnlyList<IReadOnlyList<SearchTerm>> groups, IReadOnlyList<string> phraseTokens)
    {
        Groups = groups;
        PhraseTokens = phraseTokens;
    }

    /// <summary>OR of groups; each group ANDs its terms.</summary>
    public IReadOnlyList<IReadOnlyList<SearchTerm>> Groups { get; }

    /// <summary>The tokens of the phrase boost (empty when the query has operators or fewer than two tokens).</summary>
    public IReadOnlyList<string> PhraseTokens { get; }

    /// <summary>Whether there is nothing to match (no non-negated term in any group).</summary>
    public bool IsEmpty => Groups.All(g => g.All(t => t.Negated));

    /// <param name="raw">The text as typed.</param>
    /// <param name="prefixLast">Whether the last bare token is a prefix.</param>
    public static SearchText Parse(string? raw, bool prefixLast = true)
    {
        string text = (raw ?? string.Empty).SafeNormalize(NormalizationForm.FormC).Trim();
        if (text.Length > MaxLength) text = text[..MaxLength];
        bool endsWithSpace = !string.IsNullOrEmpty(raw) && char.IsWhiteSpace(raw[^1]);

        var groups = new List<List<SearchTerm>> { new() };
        bool hasOperators = false;
        int tokenCount = 0;
        int i = 0;
        while (i < text.Length && tokenCount < MaxTokens)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            bool negated = false;
            if (text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                negated = true;
                i++;
            }
            string word;
            bool quoted = false;
            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                word = end < 0 ? text[(i + 1)..] : text[(i + 1)..end];
                i = end < 0 ? text.Length : end + 1;
                quoted = true;
            }
            else
            {
                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                word = text[start..i];
            }

            if (!quoted && !negated && word.Equals("or", StringComparison.OrdinalIgnoreCase))
            {
                hasOperators = true;
                if (groups[^1].Count > 0) groups.Add([]);
                continue;
            }
            var tokens = Tokens(word).Take(MaxTokens - tokenCount).ToList();
            if (tokens.Count == 0) continue;
            tokenCount += tokens.Count;
            if (quoted || negated)
            {
                hasOperators = true;
                groups[^1].Add(new SearchTerm(tokens, negated));
            }
            else
            {
                // "học-sinh" or "29.990.000" give several tokens: each is its own term (AND).
                groups[^1].AddRange(tokens.Select(t => new SearchTerm([t])));
            }
        }
        if (groups.Count > 1 && groups[^1].Count == 0) groups.RemoveAt(groups.Count - 1);

        // The last bare token completes as a prefix (autocomplete), unless the user typed a space after it.
        // A single letter is too broad a prefix ("m" would match "mcp"): it matches as a whole token.
        if (prefixLast && !endsWithSpace && groups[^1].Count > 0 && groups[^1][^1] is { Negated: false, IsPhrase: false } last &&
            last.Tokens[0].Length >= MinPrefixLength && text.Length > 0 && text[^1] != '"')
        {
            groups[^1][^1] = last with { Prefix = true };
        }

        List<string> phrase = !hasOperators && groups.Count == 1 && groups[0].Count >= 2
            ? groups[0].Select(t => t.Tokens[0]).ToList()
            : [];
        return new SearchText(groups.Where(g => g.Count > 0).Select(g => (IReadOnlyList<SearchTerm>)g).ToList(), phrase);
    }

    /// <summary>Lowercase, unaccented runs of <c>[a-z0-9]</c>.</summary>
    public static IEnumerable<string> Tokens(string text)
    {
        string norm = TextNormalizer.Normalize(text);
        var sb = new StringBuilder();
        foreach (char c in norm)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                yield return sb.ToString();
                sb.Clear();
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>PostgreSQL <c>to_tsquery</c> text: <c>(hoc &amp; sinh:*)</c>, phrases with <c>&lt;-&gt;</c>, <c>!</c>, <c>|</c>.</summary>
    public string ToTsQuery()
    {
        // A group of negations only ("a OR -b") is dropped: "!b" alone would match almost everything.
        var groups = Groups.Where(g => g.Any(t => !t.Negated)).Select(g =>
        {
            var parts = g.Select(t =>
            {
                string body = string.Join(" <-> ", t.Tokens.Select((tok, i) => t.Prefix && i == t.Tokens.Count - 1 ? tok + ":*" : tok));
                if (t.Tokens.Count > 1) body = "(" + body + ")";
                return t.Negated ? "!" + body : body;
            }).ToList();
            return parts.Count == 1 ? parts[0] : "(" + string.Join(" & ", parts) + ")";
        }).ToList();
        return string.Join(" | ", groups);
    }

    /// <summary>The boost phrase as a tsquery (<c>hoc &lt;-&gt; sinh:*</c>); null without one.</summary>
    public string? PhraseTsQuery()
    {
        if (PhraseTokens.Count < 2) return null;
        bool lastPrefix = Groups[0][^1].Prefix;
        return string.Join(" <-> ", PhraseTokens.Select((t, i) => lastPrefix && i == PhraseTokens.Count - 1 ? t + ":*" : t));
    }
}
