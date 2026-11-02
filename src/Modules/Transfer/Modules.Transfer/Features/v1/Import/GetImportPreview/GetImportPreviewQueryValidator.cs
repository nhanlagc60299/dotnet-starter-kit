using FluentValidation;
using FSH.Modules.Transfer.Contracts.v1.Import;

namespace FSH.Modules.Transfer.Features.v1.Import.GetImportPreview;

public sealed class GetImportPreviewQueryValidator : AbstractValidator<GetImportPreviewQuery>
{
    public GetImportPreviewQueryValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Mode).IsInEnum();
    }
}
