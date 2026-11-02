using FluentValidation;
using FSH.Modules.Search.Contracts.v1.Documents;

namespace FSH.Modules.Search.Features.v1.Documents.SearchDocuments;

public sealed class SearchDocumentsQueryValidator : AbstractValidator<SearchDocumentsQuery>
{
    public SearchDocumentsQueryValidator()
    {
        RuleFor(x => x.Q)
            .NotEmpty()
            .MaximumLength(SearchText.MaxLength)
            .Must(q => !SearchText.Parse(q).IsEmpty)
            .WithMessage("Search text must contain at least one word to match.");
        RuleFor(x => x.EntityType).MaximumLength(100);
        RuleFor(x => x.PageNumber).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}
