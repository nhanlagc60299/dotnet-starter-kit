using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Transfer.Archive;
using FSH.Modules.Transfer.Contracts;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Domain;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Transfer.Services;

/// <summary>Re-reads an import's stored archive (SHA-256 checked against the job, then fully re-validated) and feeds each entity type to its sink.</summary>
public sealed class ImportRunner(ArchiveStore store, TransferCatalog catalog, IOptions<TransferOptions> options)
{
    public async Task<TransferSummary> RunAsync(TransferJob job, TransferMode mode, bool apply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var limits = options.Value;
        var temp = TempArchive.Create();
        await using (temp.ConfigureAwait(false))
        {
            var source = await store.OpenAsync(job.StoragePath!, cancellationToken).ConfigureAwait(false)
                ?? throw new NotFoundException("The archive of this import is missing from storage.");
            string sha;
            await using (source.ConfigureAwait(false))
            {
                sha = await TempArchive.CopyAsync(source, temp, limits.MaxUploadBytes, cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(sha, job.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new CustomException("The stored archive no longer matches its SHA-256.", (IEnumerable<string>?)null, HttpStatusCode.Conflict);
            }

            using var archive = TransferArchive.Open(temp, limits);
            var rows = new Dictionary<string, int>(StringComparer.Ordinal);
            var errors = new List<TransferRowError>();
            int created = 0, updated = 0, skipped = 0, failed = 0;
            foreach (var file in archive.Manifest.Files)
            {
                var sink = catalog.Sink(file.EntityType);
                var input = archive.ReadRowsAsync(file.EntityType);
                rows[file.EntityType] = file.Rows;

                (int c, int u, int s, int f, IReadOnlyList<TransferRowError> e) = apply
                    ? Unpack(await sink.ApplyAsync(input, mode, cancellationToken).ConfigureAwait(false))
                    : Unpack(await sink.PreviewAsync(input, mode, cancellationToken).ConfigureAwait(false));
                created += c;
                updated += u;
                skipped += s;
                failed += f;
                errors.AddRange(e.Take(TransferTally.MaxErrors - errors.Count));
            }

            return new TransferSummary(rows, created, updated, skipped, failed, errors);
        }
    }

    private static (int, int, int, int, IReadOnlyList<TransferRowError>) Unpack(TransferResult r) =>
        (r.Created, r.Updated, r.Skipped, r.Failed, r.Errors);

    private static (int, int, int, int, IReadOnlyList<TransferRowError>) Unpack(TransferPreview p) =>
        (p.Created, p.Updated, p.Skipped, p.Failed, p.Errors);
}
