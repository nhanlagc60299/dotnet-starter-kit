using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.Search.Contracts.Authorization;
using FSH.Modules.Search.Contracts.v1.Documents;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Search.Features.v1.Documents.IndexSearchDocument;

public static class IndexSearchDocumentEndpoint
{
    internal static RouteHandlerBuilder MapIndexSearchDocumentEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPut("/documents",
                async (IndexSearchDocumentCommand command, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(command, ct);
                    return Results.NoContent();
                })
            .WithName("IndexSearchDocument")
            .WithSummary("Add or replace the search index entry for an entity")
            .Produces(StatusCodes.Status204NoContent)
            .RequirePermission(SearchPermissions.Search.Manage);
    }
}
