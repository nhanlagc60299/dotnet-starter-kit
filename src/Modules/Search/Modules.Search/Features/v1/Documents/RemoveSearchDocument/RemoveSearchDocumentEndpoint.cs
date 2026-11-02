using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Search.Contracts.Authorization;
using FSH.Modules.Search.Contracts.v1.Documents;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Search.Features.v1.Documents.RemoveSearchDocument;

public static class RemoveSearchDocumentEndpoint
{
    internal static RouteHandlerBuilder MapRemoveSearchDocumentEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/documents/{entityType}/{entityId}",
                async (string entityType, string entityId, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new RemoveSearchDocumentCommand(entityType, entityId), ct);
                    return Results.NoContent();
                })
            .WithName("RemoveSearchDocument")
            .WithSummary("Remove the search index entry for an entity (no-op if absent)")
            .Produces(StatusCodes.Status204NoContent)
            .RequirePermission(SearchPermissions.Search.Manage);
    }
}
