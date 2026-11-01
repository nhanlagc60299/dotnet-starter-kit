using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.AccessControl.Contracts.Authorization;
using FSH.Modules.AccessControl.Contracts.Dtos;
using FSH.Modules.AccessControl.Contracts.v1.Grants;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.AccessControl.Features.v1.Grants.GetRoleGrants;

public static class GetRoleGrantsEndpoint
{
    internal static RouteHandlerBuilder MapGetRoleGrantsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/roles/{role}/grants",
                async (string role, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GetRoleGrantsQuery(role), ct)))
            .WithName("GetRoleGrants")
            .WithSummary("List the access grants of a role")
            .Produces<IReadOnlyList<AccessGrantDto>>()
            .RequirePermission(AccessControlPermissions.AccessControl.View);
    }
}
