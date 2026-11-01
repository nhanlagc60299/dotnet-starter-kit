using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.AccessControl.Contracts.v1.Grants;
using FSH.Modules.AccessControl.Data;
using FSH.Modules.AccessControl.Domain;
using FSH.Modules.AccessControl.Services;
using FSH.Modules.Identity.Contracts.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.AccessControl.Features.v1.Grants.UpdateRoleGrants;

public sealed class UpdateRoleGrantsCommandHandler(
    AccessControlDbContext dbContext,
    IRoleService roleService,
    ICurrentUser currentUser,
    GrantStore store) : ICommandHandler<UpdateRoleGrantsCommand, Unit>
{
    public async ValueTask<Unit> Handle(UpdateRoleGrantsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string role = await RoleNames.ResolveAsync(roleService, command.Role, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.AccessGrants
            .Where(g => g.Role == role)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // {} means "no condition", like null.
        var wanted = command.Grants
            .Select(g => new CachedGrant(g.Subject, g.Action, g.Condition is { Count: > 0 } c ? c.ToJsonString() : null))
            .ToList();

        var callerRoles = AccessRules.Roles(currentUser);
        if (!AccessRules.IsAdmin(callerRoles))
        {
            var mine = new List<CachedGrant>();
            foreach (string callerRole in callerRoles)
                mine.AddRange(await store.GetAsync(callerRole, cancellationToken).ConfigureAwait(false));

            var before = existing.Select(g => new CachedGrant(g.Subject, g.Action, g.Condition)).ToList();
            if (AccessRules.FirstUncovered(mine, AccessRules.Changed(before, wanted)) is { } uncovered)
            {
                throw new ForbiddenException(
                    $"You can only change grants for actions you hold without a condition; you cannot change '{uncovered.Action}' on '{uncovered.Subject}'.");
            }
        }

        foreach (var grant in existing.Where(e => !wanted.Exists(w => w.Subject == e.Subject && w.Action == e.Action)))
            dbContext.AccessGrants.Remove(grant);
        foreach (var grant in wanted)
        {
            var current = existing.Find(e => e.Subject == grant.Subject && e.Action == grant.Action);
            if (current is null)
                dbContext.AccessGrants.Add(AccessGrant.Create(role, grant.Subject, grant.Action, grant.Condition));
            else
                current.SetCondition(grant.Condition);
        }

        // ponytail: two concurrent PUTs for the same role can race to the unique index, and one gets a 500
        // (retrying is safe). Take a per-role advisory lock if roles are ever edited concurrently.
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await store.InvalidateAsync(role, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
