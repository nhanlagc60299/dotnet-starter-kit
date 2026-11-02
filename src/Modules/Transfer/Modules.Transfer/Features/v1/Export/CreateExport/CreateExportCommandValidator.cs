using FluentValidation;
using FSH.Modules.Transfer.Archive;
using FSH.Modules.Transfer.Contracts.v1.Export;

namespace FSH.Modules.Transfer.Features.v1.Export.CreateExport;

public sealed class CreateExportCommandValidator : AbstractValidator<CreateExportCommand>
{
    public CreateExportCommandValidator()
    {
        RuleFor(x => x.EntityTypes).NotEmpty()
            .Must(t => t.Count <= 100).WithMessage("At most 100 entity types per export.")
            .Must(t => t.Distinct(StringComparer.Ordinal).Count() == t.Count).WithMessage("Entity types must be distinct.");
        RuleForEach(x => x.EntityTypes)
            .Must(TransferFormat.IsValidEntityType).WithMessage("'{PropertyValue}' is not a valid entity type.");
    }
}
