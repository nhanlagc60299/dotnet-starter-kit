using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Jobs.Services;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Export;
using FSH.Modules.Transfer.Data;
using FSH.Modules.Transfer.Domain;
using FSH.Modules.Transfer.Services;
using Mediator;

namespace FSH.Modules.Transfer.Features.v1.Export.CreateExport;

public sealed class CreateExportCommandHandler(
    TransferDbContext dbContext,
    TransferCatalog catalog,
    ICurrentUser currentUser,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    IJobService jobService) : ICommandHandler<CreateExportCommand, TransferJobDto>
{
    public async ValueTask<TransferJobDto> Handle(CreateExportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await catalog.DemandExportAsync(command.EntityTypes, cancellationToken).ConfigureAwait(false);

        string tenantId = TenantIds.Current(tenantAccessor);
        var job = TransferJob.CreateExport(command.EntityTypes, currentUser.GetUserId().ToString());
        dbContext.TransferJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            jobService.Enqueue<TransferJobRunner>(r => r.RunExportAsync(job.Id, tenantId, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never leave a job Pending that no worker will pick up.
            job.Fail("The export could not be queued. Try again.");
            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return job.ToDto();
    }
}
