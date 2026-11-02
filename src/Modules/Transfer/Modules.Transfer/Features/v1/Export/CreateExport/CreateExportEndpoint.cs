using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Transfer.Contracts.Authorization;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Export;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Transfer.Features.v1.Export.CreateExport;

public static class CreateExportEndpoint
{
    internal static RouteHandlerBuilder MapCreateExportEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/exports",
                async (CreateExportCommand command, IMediator mediator, CancellationToken ct) =>
                {
                    var job = await mediator.Send(command, ct);
                    return Results.Accepted($"/api/v1/transfer/jobs/{job.Id}", job);
                })
            .WithName("CreateExport")
            .WithSummary("Queue an export of the given entity types")
            .Produces<TransferJobDto>(StatusCodes.Status202Accepted)
            .RequirePermission(TransferPermissions.Transfer.Export);
    }
}
