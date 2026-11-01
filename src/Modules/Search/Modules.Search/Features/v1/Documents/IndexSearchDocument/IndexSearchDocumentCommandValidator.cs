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
        RuleFor(x => x.Url).MaximumLength(1000);
    }
}
