using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Transfer.Contracts.Authorization;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Import;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Transfer.Features.v1.Import.StartImport;

public static class StartImportEndpoint
{
    internal static RouteHandlerBuilder MapStartImportEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/imports/{id:guid}/apply",
                async (Guid id, ImportModeRequest request, IMediator mediator, CancellationToken ct) =>
                {
                    var job = await mediator.Send(new StartImportCommand(id, request.Mode), ct);
                    return Results.Accepted($"/api/v1/transfer/jobs/{job.Id}", job);
                })
            .WithName("StartImport")
            .WithSummary("Queue applying an uploaded archive (create or upsert; never deletes)")
            .Produces<TransferJobDto>(StatusCodes.Status202Accepted)
            .RequirePermission(TransferPermissions.Transfer.Import);
    }
}
