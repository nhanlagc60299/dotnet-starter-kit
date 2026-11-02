using FluentValidation;
using FSH.Modules.Search.Contracts.v1.Documents;

namespace FSH.Modules.Search.Features.v1.Documents.RemoveSearchDocument;

public sealed class RemoveSearchDocumentCommandValidator : AbstractValidator<RemoveSearchDocumentCommand>
{
    public RemoveSearchDocumentCommandValidator()
    {
        RuleFor(x => x.EntityType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.EntityId).NotEmpty().MaximumLength(100);
    }
}
