using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.EntityTypes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Transfer.Features.v1.EntityTypes.GetTransferEntityTypes;

public static class GetTransferEntityTypesEndpoint
{
    // Any signed-in user: the flags say what the caller may do, so the list itself needs no permission.
    internal static RouteHandlerBuilder MapGetTransferEntityTypesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/entity-types",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GetTransferEntityTypesQuery(), ct)))
            .WithName("GetTransferEntityTypes")
            .WithSummary("List the entity types that can be exported or imported")
            .Produces<IReadOnlyList<TransferEntityTypeDto>>();
    }
}
