using Asp.Versioning;
using FSH.Framework.Persistence;
using FSH.Framework.Shared.Constants;
using FSH.Framework.Web.Modules;
using FSH.Modules.Transfer.Contracts.Authorization;
using FSH.Modules.Transfer.Data;
using FSH.Modules.Transfer.Features.v1.EntityTypes.GetTransferEntityTypes;
using FSH.Modules.Transfer.Features.v1.Export.CreateExport;
using FSH.Modules.Transfer.Features.v1.Export.GetExportArchive;
using FSH.Modules.Transfer.Features.v1.Import.CreateImport;
using FSH.Modules.Transfer.Features.v1.Import.GetImportPreview;
using FSH.Modules.Transfer.Features.v1.Import.StartImport;
using FSH.Modules.Transfer.Features.v1.Jobs.GetTransferJob;
using FSH.Modules.Transfer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

[assembly: FshModule(typeof(FSH.Modules.Transfer.TransferModule), 920)]

namespace FSH.Modules.Transfer;

public sealed class TransferModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        PermissionConstants.Register(TransferPermissions.All);

        builder.Services.AddOptions<TransferOptions>()
            .BindConfiguration(nameof(TransferOptions))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddHeroDbContext<TransferDbContext>();
        builder.Services.AddScoped<IDbInitializer, TransferDbInitializer>();
        builder.Services.AddScoped<ArchiveStore>();
        builder.Services.AddScoped<TransferCatalog>();
        builder.Services.AddScoped<ImportRunner>();
        builder.Services.AddTransient<TransferJobRunner>();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<TransferDbContext>(
                name: "db:transfer",
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
            .MapGroup("api/v{version:apiVersion}/transfer")
            .WithTags("Transfer")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();

        long maxUploadBytes = endpoints.ServiceProvider.GetRequiredService<IOptions<TransferOptions>>().Value.MaxUploadBytes;

        group.MapGetTransferEntityTypesEndpoint();
        group.MapCreateExportEndpoint();
        group.MapGetExportArchiveEndpoint();
        group.MapGetTransferJobEndpoint();
        group.MapCreateImportEndpoint(maxUploadBytes);
        group.MapGetImportPreviewEndpoint();
        group.MapStartImportEndpoint();
    }
}
