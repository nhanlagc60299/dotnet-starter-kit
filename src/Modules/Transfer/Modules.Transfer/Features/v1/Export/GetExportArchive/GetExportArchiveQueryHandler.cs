using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Export;
using FSH.Modules.Transfer.Data;
using FSH.Modules.Transfer.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Transfer.Features.v1.Export.GetExportArchive;

public sealed class GetExportArchiveQueryHandler(TransferDbContext dbContext, TransferCatalog catalog, ArchiveStore store)
    : IQueryHandler<GetExportArchiveQuery, TransferArchiveFile>
{
    public async ValueTask<TransferArchiveFile> Handle(GetExportArchiveQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var job = await dbContext.TransferJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == query.Id && j.Kind == TransferJobKind.Export, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Export {query.Id} not found.");

        // The archive holds rows of every exported type, so the caller needs each source's permission too.
        await catalog.DemandExportAsync(job.EntityTypes, cancellationToken).ConfigureAwait(false);

        if (job.Status != TransferJobStatus.Succeeded)
        {
            throw new CustomException($"Export {query.Id} is {job.Status}, not Succeeded.", (IEnumerable<string>?)null, HttpStatusCode.Conflict);
        }

        var content = await store.OpenAsync(job.StoragePath!, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"The archive of export {query.Id} is missing from storage.");
        return new TransferArchiveFile(content, job.FileName!, job.SizeBytes);
    }
}
