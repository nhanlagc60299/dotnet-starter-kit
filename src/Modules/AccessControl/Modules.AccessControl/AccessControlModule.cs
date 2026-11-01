using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using FSH.Modules.AccessControl.Contracts;
using FSH.Modules.AccessControl.Contracts.Authorization;
using FSH.Modules.AccessControl.Data;
using FSH.Modules.AccessControl.Features.v1.Grants.GetRoleGrants;
using FSH.Modules.AccessControl.Features.v1.Grants.UpdateRoleGrants;
using FSH.Modules.AccessControl.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

[assembly: FshModule(typeof(FSH.Modules.AccessControl.AccessControlModule), 910)]

namespace FSH.Modules.AccessControl;

public sealed class AccessControlModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        PermissionConstants.Register(AccessControlPermissions.All);

        builder.Services.AddHeroDbContext<AccessControlDbContext>();
        builder.Services.AddScoped<IDbInitializer, AccessControlDbInitializer>();
        builder.Services.AddScoped<GrantStore>();
        builder.Services.AddScoped<IAccessFilter, AccessFilter>();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<AccessControlDbContext>(
                name: "db:access-control",
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
            .MapGroup("api/v{version:apiVersion}/access-control")
            .WithTags("AccessControl")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        group.MapGetRoleGrantsEndpoint();
        group.MapUpdateRoleGrantsEndpoint();
    }
}
