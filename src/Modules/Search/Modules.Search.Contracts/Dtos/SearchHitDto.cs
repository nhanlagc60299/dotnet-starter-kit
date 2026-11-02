namespace FSH.Modules.Search.Contracts.Dtos;

/// <summary>
/// One search result. <paramref name="Snippet"/> is a fragment of the summary/body with the matched
/// words wrapped in <c>&lt;mark&gt;</c>…<c>&lt;/mark&gt;</c>; the rest is the indexed text as-is (not
/// HTML-encoded), so render it by splitting on the markers, never as raw HTML.
/// </summary>
public sealed record SearchHitDto(
    string EntityType,
    string EntityId,
    string Title,
    string? Snippet,
    string? Url,
    float Rank);
