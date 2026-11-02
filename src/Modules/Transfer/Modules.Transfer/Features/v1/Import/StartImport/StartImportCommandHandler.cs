using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Import;
using FSH.Modules.Transfer.Data;
using FSH.Modules.Transfer.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Transfer.Features.v1.Import.StartImport;

public sealed class StartImportCommandHandler(
    TransferDbContext dbContext,
    TransferCatalog catalog,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    IJobService jobService) : ICommandHandler<StartImportCommand, TransferJobDto>
{
    public async ValueTask<TransferJobDto> Handle(StartImportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await dbContext.TransferJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == command.Id && j.Kind == TransferJobKind.Import, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Import {command.Id} not found.");

        await catalog.DemandImportAsync(job.EntityTypes, cancellationToken).ConfigureAwait(false);
        string tenantId = TenantIds.Current(tenantAccessor);

        // Claim atomically so two clicks cannot apply the same archive twice. A failed import may be started again.
        int claimed = await dbContext.TransferJobs
            .Where(j => j.Id == command.Id
                && j.Kind == TransferJobKind.Import
                && (j.Status == TransferJobStatus.Pending || j.Status == TransferJobStatus.Failed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, TransferJobStatus.Running)
                .SetProperty(j => j.Mode, command.Mode)
                .SetProperty(j => j.Error, (string?)null)
                .SetProperty(j => j.CompletedAtUtc, (DateTime?)null), cancellationToken)
            .ConfigureAwait(false);
        if (claimed == 0)
        {
            throw new CustomException($"Import {command.Id} is {job.Status}; only Pending or Failed imports can be started.",
                (IEnumerable<string>?)null, HttpStatusCode.Conflict);
        }

        try
        {
            jobService.Enqueue<TransferJobRunner>(r => r.RunImportAsync(command.Id, tenantId, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Release the claim (as Failed, which may be started again) instead of leaving it Running forever.
            await dbContext.TransferJobs.Where(j => j.Id == command.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, TransferJobStatus.Failed)
                    .SetProperty(j => j.Error, "The import could not be queued. Try again."), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        var started = await dbContext.TransferJobs.AsNoTracking()
            .FirstAsync(j => j.Id == command.Id, cancellationToken)
            .ConfigureAwait(false);
        return started.ToDto();
    }
}
