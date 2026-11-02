using FluentValidation;
using FSH.Modules.Transfer.Contracts.v1.Import;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Transfer.Features.v1.Import.CreateImport;

public sealed class CreateImportCommandValidator : AbstractValidator<CreateImportCommand>
{
    public CreateImportCommandValidator(IOptions<TransferOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        long max = options.Value.MaxUploadBytes;
        RuleFor(x => x.Content).NotNull();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Length).GreaterThan(0).LessThanOrEqualTo(max)
            .WithMessage($"The archive must be between 1 and {max} bytes.");
    }
}
