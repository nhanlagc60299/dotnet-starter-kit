using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using FSH.Modules.Search.Contracts.Authorization;
using FSH.Modules.Search.Data;
using FSH.Modules.Search.Features.v1.Documents.IndexSearchDocument;
using FSH.Modules.Search.Features.v1.Documents.RemoveSearchDocument;
using FSH.Modules.Search.Features.v1.Documents.SearchDocuments;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

[assembly: FshModule(typeof(FSH.Modules.Search.SearchModule), 900)]

namespace FSH.Modules.Search;

public sealed class SearchModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        PermissionConstants.Register(SearchPermissions.All);

        builder.Services.AddHeroDbContext<SearchDbContext>();
        builder.Services.AddScoped<IDbInitializer, SearchDbInitializer>();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<SearchDbContext>(
                name: "db:search",
                failureStatus: HealthStatus.Unhealthy);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        // No custom middleware needed
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints
            .MapGroup("api/v{version:apiVersion}/search")
            .WithTags("Search")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        group.MapSearchDocumentsEndpoint();
        group.MapIndexSearchDocumentEndpoint();
        group.MapRemoveSearchDocumentEndpoint();
    }
}
