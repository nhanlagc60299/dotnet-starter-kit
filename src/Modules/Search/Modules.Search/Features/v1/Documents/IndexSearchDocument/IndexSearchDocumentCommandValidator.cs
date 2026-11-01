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

    // The UI renders Url as a link, so anything else (javascript:, data:, protocol-relative //host) is refused.
    internal static bool BeSafeLink(string? url) =>
        url is null
        || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal))
        || (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
}
