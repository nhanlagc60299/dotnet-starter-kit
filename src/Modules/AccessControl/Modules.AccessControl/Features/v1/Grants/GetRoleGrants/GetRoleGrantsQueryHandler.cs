using System.Text.Json.Nodes;
using FSH.Modules.AccessControl.Contracts.Dtos;
using FSH.Modules.AccessControl.Contracts.v1.Grants;
using FSH.Modules.AccessControl.Data;
using FSH.Modules.AccessControl.Services;
using FSH.Modules.Identity.Contracts.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.AccessControl.Features.v1.Grants.GetRoleGrants;

public sealed class GetRoleGrantsQueryHandler(AccessControlDbContext dbContext, IRoleService roleService)
    : IQueryHandler<GetRoleGrantsQuery, IReadOnlyList<AccessGrantDto>>
{
    public async ValueTask<IReadOnlyList<AccessGrantDto>> Handle(GetRoleGrantsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        string role = await RoleNames.ResolveAsync(roleService, query.Role, cancellationToken).ConfigureAwait(false);

        var rows = await dbContext.AccessGrants.AsNoTracking()
            .Where(g => g.Role == role)
            .OrderBy(g => g.Subject).ThenBy(g => g.Action)
            .Select(g => new { g.Subject, g.Action, g.Condition })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r => new AccessGrantDto(r.Subject, r.Action,
            r.Condition is null ? null : JsonNode.Parse(r.Condition) as JsonObject)).ToList();
    }
}
