using FluentValidation;
using FSH.Modules.Transfer.Contracts.v1.Import;

namespace FSH.Modules.Transfer.Features.v1.Import.StartImport;

public sealed class StartImportCommandValidator : AbstractValidator<StartImportCommand>
{
    public StartImportCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Mode).IsInEnum();
    }
}
