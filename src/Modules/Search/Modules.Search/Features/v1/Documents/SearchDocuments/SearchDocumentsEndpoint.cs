using FSH.Framework.Shared.Identity.Authorization;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Search.Contracts.Authorization;
using FSH.Modules.Search.Contracts.Dtos;
using FSH.Modules.Search.Contracts.v1.Documents;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Search.Features.v1.Documents.SearchDocuments;

public static class SearchDocumentsEndpoint
{
    internal static RouteHandlerBuilder MapSearchDocumentsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/",
                async (string? q, string? entityType, int? pageNumber, int? pageSize, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(
                        new SearchDocumentsQuery(q ?? string.Empty, entityType, pageNumber ?? 1, pageSize ?? 20), ct)))
            .WithName("SearchDocuments")
            .WithSummary("Full-text search the tenant's index (Vietnamese, accent-insensitive)")
            .Produces<PagedResponse<SearchHitDto>>()
            .RequirePermission(SearchPermissions.Search.View);
    }
}
