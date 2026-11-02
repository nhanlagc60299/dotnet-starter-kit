using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Jobs;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Transfer.Features.v1.Jobs.GetTransferJob;

public static class GetTransferJobEndpoint
{
    // Transfer.Export or Transfer.Import depending on the job's kind: checked by the handler.
    internal static RouteHandlerBuilder MapGetTransferJobEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/jobs/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GetTransferJobQuery(id), ct)))
            .WithName("GetTransferJob")
            .WithSummary("Get an export or import job")
            .Produces<TransferJobDto>();
    }
}
