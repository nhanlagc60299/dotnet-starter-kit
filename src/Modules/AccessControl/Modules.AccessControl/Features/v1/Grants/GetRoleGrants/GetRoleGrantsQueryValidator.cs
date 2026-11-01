using FluentValidation;
using FSH.Modules.AccessControl.Contracts.v1.Grants;

namespace FSH.Modules.AccessControl.Features.v1.Grants.GetRoleGrants;

public sealed class GetRoleGrantsQueryValidator : AbstractValidator<GetRoleGrantsQuery>
{
    public GetRoleGrantsQueryValidator()
    {
        RuleFor(x => x.Role).NotEmpty().MaximumLength(256);
    }
}
