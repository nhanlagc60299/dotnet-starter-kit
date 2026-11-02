using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Transfer.Contracts.Authorization;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Import;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Transfer.Features.v1.Import.CreateImport;

public static class CreateImportEndpoint
{
    /// <param name="endpoints">The Transfer route group.</param>
    /// <param name="maxUploadBytes">From <see cref="TransferOptions.MaxUploadBytes"/>; the handler re-counts the bytes received.</param>
    internal static RouteHandlerBuilder MapCreateImportEndpoint(this IEndpointRouteBuilder endpoints, long maxUploadBytes)
    {
        // Multipart form field "file". Bearer-token API, so no antiforgery; no idempotency (large body, see security.md).
        return endpoints.MapPost("/imports",
                async (IFormFile file, IMediator mediator, CancellationToken ct) =>
                {
                    var content = file.OpenReadStream();
                    await using (content.ConfigureAwait(false))
                    {
                        return Results.Ok(await mediator.Send(new CreateImportCommand(content, file.FileName, file.Length), ct));
                    }
                })
            .WithName("CreateImport")
            .WithSummary("Upload an archive: validated and stored, nothing imported yet")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<TransferJobDto>()
            .DisableAntiforgery()
            .WithFormOptions(multipartBodyLengthLimit: maxUploadBytes)
            .WithMetadata(new RequestSizeLimitAttribute(maxUploadBytes + (1 << 20)))
            .RequirePermission(TransferPermissions.Transfer.Import);
    }
}
