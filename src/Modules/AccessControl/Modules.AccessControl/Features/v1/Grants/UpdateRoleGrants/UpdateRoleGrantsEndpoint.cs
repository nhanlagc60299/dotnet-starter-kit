using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.AccessControl.Contracts.Authorization;
using FSH.Modules.AccessControl.Contracts.Dtos;
using FSH.Modules.AccessControl.Contracts.v1.Grants;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.AccessControl.Features.v1.Grants.UpdateRoleGrants;

public static class UpdateRoleGrantsEndpoint
{
    internal static RouteHandlerBuilder MapUpdateRoleGrantsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/roles/{role}/grants",
                async (string role, IReadOnlyList<AccessGrantDto> grants, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new UpdateRoleGrantsCommand(role, grants), ct);
                    return Results.NoContent();
                })
            .WithName("UpdateRoleGrants")
            .WithSummary("Replace the access grants of a role")
            .Produces(StatusCodes.Status204NoContent)
            .RequirePermission(AccessControlPermissions.AccessControl.Manage);
    }
}
