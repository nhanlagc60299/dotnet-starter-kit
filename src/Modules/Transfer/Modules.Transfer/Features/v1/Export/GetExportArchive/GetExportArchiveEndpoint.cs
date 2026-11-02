using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Transfer.Contracts.Authorization;
using FSH.Modules.Transfer.Contracts.v1.Export;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Transfer.Features.v1.Export.GetExportArchive;

public static class GetExportArchiveEndpoint
{
    internal static RouteHandlerBuilder MapGetExportArchiveEndpoint(this IEndpointRouteBuilder endpoints)
    {
        // The file name is generated server-side (never client input); Results.Stream encodes Content-Disposition.
        return endpoints.MapGet("/exports/{id:guid}/download",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                {
                    var file = await mediator.Send(new GetExportArchiveQuery(id), ct);
                    return Results.Stream(file.Content, "application/zip", file.FileName);
                })
            .WithName("GetExportArchive")
            .WithSummary("Download the archive of a succeeded export")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .RequirePermission(TransferPermissions.Transfer.Export);
    }
}
