using System.Text.Json.Nodes;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Transfer.Archive;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Data;
using FSH.Modules.Transfer.Domain;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Transfer.Services;

/// <summary>
/// Hangfire jobs that run an export, or apply an import claimed by <c>StartImport</c>. Each run opens a fresh scope with
/// the job's tenant and creator set before anything tenant-filtered is resolved. No automatic retries: a failure is
/// recorded on the job (the user can start an import again), and nothing is ever cleaned up automatically.
/// </summary>
public sealed class TransferJobRunner(IServiceScopeFactory scopeFactory, ILogger<TransferJobRunner> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RunExportAsync(Guid jobId, string tenantId, CancellationToken cancellationToken) =>
        RunAsync(jobId, tenantId, TransferJobKind.Export, cancellationToken);

    [AutomaticRetry(Attempts = 0)]
    public Task RunImportAsync(Guid jobId, string tenantId, CancellationToken cancellationToken) =>
        RunAsync(jobId, tenantId, TransferJobKind.Import, cancellationToken);

    private async Task RunAsync(Guid jobId, string tenantId, TransferJobKind kind, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var tenant = await services.GetRequiredService<IMultiTenantStore<AppTenantInfo>>().GetAsync(tenantId).ConfigureAwait(false);
        if (tenant is null)
        {
            logger.LogWarning("Skipping transfer job {JobId}: tenant {TenantId} not found", jobId, tenantId);
            return;
        }

        services.GetRequiredService<IMultiTenantContextSetter>().MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);

        var db = services.GetRequiredService<TransferDbContext>();
        var job = await db.TransferJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.Kind == kind, cancellationToken).ConfigureAwait(false);

        // An export runs from Pending (or Running again after a crash); an import only once StartImport claimed it.
        bool runnable = job is not null
            && (job.Status == TransferJobStatus.Running || (kind == TransferJobKind.Export && job.Status == TransferJobStatus.Pending));
        if (!runnable)
        {
            logger.LogWarning("Skipping transfer job {JobId}: not found or not runnable", jobId);
            return;
        }

        services.GetRequiredService<ICurrentUserInitializer>().SetCurrentUserId(job!.CreatedBy);
        if (job.Status == TransferJobStatus.Pending)
        {
            job.MarkRunning();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Permissions were checked when the job was requested; the creator may have lost them since.
            var catalog = services.GetRequiredService<TransferCatalog>();
            await (kind == TransferJobKind.Export
                ? catalog.DemandExportAsync(job.EntityTypes, cancellationToken)
                : catalog.DemandImportAsync(job.EntityTypes, cancellationToken)).ConfigureAwait(false);

            if (kind == TransferJobKind.Export)
            {
                await ExportAsync(services, job, tenant.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var summary = await services.GetRequiredService<ImportRunner>()
                    .RunAsync(job, job.Mode!.Value, apply: true, cancellationToken).ConfigureAwait(false);
                job.Complete(summary);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Transfer job {JobId} ({Kind}) failed", job.Id, kind);
            // Only our own messages reach the user; anything else may carry internals (SQL, paths).
            job.Fail(ex is CustomException ? ex.Message : $"The job failed. See the server logs for job {job.Id}.");
        }

        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task ExportAsync(IServiceProvider services, TransferJob job, string tenantId, CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<TransferCatalog>();
        var options = services.GetRequiredService<IOptions<TransferOptions>>().Value;
        var store = services.GetRequiredService<ArchiveStore>();

        List<(string EntityType, IAsyncEnumerable<JsonObject> Rows)> sources =
            [.. job.EntityTypes.Select(t => (t, catalog.Source(t).ExportAsync(cancellationToken)))];

        var temp = TempArchive.Create();
        await using (temp.ConfigureAwait(false))
        {
            var manifest = await TransferArchiveWriter
                .WriteAsync(temp, sources, DateTime.UtcNow, options, cancellationToken).ConfigureAwait(false);
            long size = temp.Length;
            string sha = await TempArchive.HashAsync(temp, cancellationToken).ConfigureAwait(false);

            string key = await store.SaveAsync(tenantId, job.Id, temp, cancellationToken).ConfigureAwait(false);

            var rows = manifest.Files.ToDictionary(f => f.EntityType, f => f.Rows, StringComparer.Ordinal);
            job.CompleteExport(key, $"transfer-export-{job.CreatedAtUtc:yyyyMMdd-HHmmss}.zip", size, sha,
                new TransferSummary(rows, 0, 0, 0, 0, []));
        }
    }
}
