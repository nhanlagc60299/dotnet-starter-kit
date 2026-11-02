using FluentValidation;
using FSH.Modules.Search.Contracts.v1.Documents;

namespace FSH.Modules.Search.Features.v1.Documents.IndexSearchDocument;

public sealed class IndexSearchDocumentCommandValidator : AbstractValidator<IndexSearchDocumentCommand>
{
    // PostgreSQL rejects a tsvector over 1 MB; capping the text keeps a large body a 400, not a failed insert.
    internal const int MaxSummaryLength = 4_000;
    internal const int MaxBodyLength = 200_000;

    public IndexSearchDocumentCommandValidator()
    {
        RuleFor(x => x.EntityType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.EntityId).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Summary).MaximumLength(MaxSummaryLength);
        RuleFor(x => x.Body).MaximumLength(MaxBodyLength);
        RuleFor(x => x.Url)
            .MaximumLength(1000)
            .Must(BeSafeLink)
            .WithMessage("Url must be a relative path starting with a single '/', or an absolute http/https URL.");
    }

    private static readonly Uri ProbeBase = new("https://h.invalid");

    // The UI renders Url as a link that every Search.View user may click, so anything that could leave the
    // app or run script is refused: javascript:/data: and other schemes, protocol-relative "//host" and
    // "/\host", and control characters or whitespace (browsers strip tab/CR/LF while parsing, which turns
    // "/\t/evil.com" into "//evil.com"). Percent-encoded bytes stay allowed: browsers do not decode them
    // into the authority, and the probe below would catch it if they did.
    internal static bool BeSafeLink(string? url)
    {
        if (url is null) return true;
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;

        if (url.StartsWith('/'))
        {
            if (url.Length > 1 && url[1] is '/' or '\\') return false;
            // Belt and braces: resolved against any origin, a relative link must stay on that origin.
            return Uri.TryCreate(ProbeBase, url, out var resolved)
                && string.Equals(resolved.Host, ProbeBase.Host, StringComparison.Ordinal);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
