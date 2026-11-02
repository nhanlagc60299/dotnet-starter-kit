using FSH.Framework.Core.Exceptions;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Import;
using FSH.Modules.Transfer.Data;
using FSH.Modules.Transfer.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Transfer.Features.v1.Import.GetImportPreview;

public sealed class GetImportPreviewQueryHandler(TransferDbContext dbContext, TransferCatalog catalog, ImportRunner runner)
    : IQueryHandler<GetImportPreviewQuery, TransferSummary>
{
    public async ValueTask<TransferSummary> Handle(GetImportPreviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var job = await dbContext.TransferJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == query.Id && j.Kind == TransferJobKind.Import, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Import {query.Id} not found.");

        await catalog.DemandImportAsync(job.EntityTypes, cancellationToken).ConfigureAwait(false);
        return await runner.RunAsync(job, query.Mode, apply: false, cancellationToken).ConfigureAwait(false);
    }
}
