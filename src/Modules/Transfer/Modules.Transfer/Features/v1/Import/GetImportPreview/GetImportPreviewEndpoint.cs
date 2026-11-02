using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Transfer.Contracts.Authorization;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Import;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Transfer.Features.v1.Import.GetImportPreview;

public static class GetImportPreviewEndpoint
{
    internal static RouteHandlerBuilder MapGetImportPreviewEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/imports/{id:guid}/preview",
                async (Guid id, ImportModeRequest request, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new GetImportPreviewQuery(id, request.Mode), ct)))
            .WithName("GetImportPreview")
            .WithSummary("Preview what applying an uploaded archive would do (writes nothing)")
            .Produces<TransferSummary>()
            .RequirePermission(TransferPermissions.Transfer.Import);
    }
}
