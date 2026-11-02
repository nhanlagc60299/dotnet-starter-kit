using FSH.Framework.Core.Exceptions;
using FSH.Modules.Transfer.Contracts.Authorization;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Jobs;
using FSH.Modules.Transfer.Data;
using FSH.Modules.Transfer.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Transfer.Features.v1.Jobs.GetTransferJob;

public sealed class GetTransferJobQueryHandler(TransferDbContext dbContext, TransferCatalog catalog)
    : IQueryHandler<GetTransferJobQuery, TransferJobDto>
{
    public async ValueTask<TransferJobDto> Handle(GetTransferJobQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var job = await dbContext.TransferJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == query.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Transfer job {query.Id} not found.");

        string permission = job.Kind == TransferJobKind.Export
            ? TransferPermissions.Transfer.Export
            : TransferPermissions.Transfer.Import;
        if (!await catalog.HasAsync(permission, cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException($"You need the {permission} permission.");
        }

        return job.ToDto();
    }
}
