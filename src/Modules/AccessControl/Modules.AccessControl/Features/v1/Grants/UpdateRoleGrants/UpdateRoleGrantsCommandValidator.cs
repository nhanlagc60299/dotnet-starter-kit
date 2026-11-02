using FluentValidation;
using FSH.Modules.AccessControl.Conditions;
using FSH.Modules.AccessControl.Contracts.Dtos;
using FSH.Modules.AccessControl.Contracts.v1.Grants;

namespace FSH.Modules.AccessControl.Features.v1.Grants.UpdateRoleGrants;

public sealed class UpdateRoleGrantsCommandValidator : AbstractValidator<UpdateRoleGrantsCommand>
{
    internal const int MaxGrants = 500;
    internal const int MaxConditionLength = 8_000;

    // The brief's patterns, anchored with \z instead of $ so a trailing newline is not accepted.
    internal const string SubjectPattern = @"^[a-z][a-z0-9-]*(:[a-z0-9*-]+)?\z";
    internal const string ActionPattern = @"^[a-z][a-zA-Z]*\z";

    public UpdateRoleGrantsCommandValidator()
    {
        RuleFor(x => x.Role).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Grants)
            .NotNull()
            .Must(g => g is null || g.Count <= MaxGrants).WithMessage($"A role may have at most {MaxGrants} grants.")
            .Must(BeDistinct).WithMessage("Each (subject, action) pair may appear only once.");
        RuleForEach(x => x.Grants).NotNull().ChildRules(grant =>
        {
            grant.RuleFor(g => g.Subject).NotEmpty().MaximumLength(100).Matches(SubjectPattern)
                .WithMessage("Subject must be lowercase letters, digits and '-', optionally followed by ':' and a name or '*'.");
            grant.RuleFor(g => g.Action).NotEmpty().MaximumLength(50).Matches(ActionPattern)
                .WithMessage("Action must be a camelCase word, e.g. 'read' or 'readDraft'.");
            grant.RuleFor(g => g.Condition).Custom((condition, context) =>
            {
                if (condition is null) return;
                try
                {
                    if (condition.ToJsonString().Length > MaxConditionLength)
                        context.AddFailure($"Condition must be at most {MaxConditionLength} characters of JSON.");
                    else if (ConditionCompiler.Validate(condition) is { } problem)
                        context.AddFailure(problem);
                }
                catch (ArgumentException)
                {
                    // JsonObject throws when first enumerated if the request repeated a property name.
                    context.AddFailure("The condition repeats a property name.");
                }
            });
        });
    }

    private static bool BeDistinct(IReadOnlyList<AccessGrantDto>? grants) =>
        grants is null || grants.Where(g => g is not null).DistinctBy(g => (g.Subject, g.Action)).Count() == grants.Count(g => g is not null);
}
