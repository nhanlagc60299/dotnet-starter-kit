using System.Text.RegularExpressions;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Transfer.Archive;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.Import;
using FSH.Modules.Transfer.Data;
using FSH.Modules.Transfer.Domain;
using FSH.Modules.Transfer.Services;
using Mediator;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Transfer.Features.v1.Import.CreateImport;

public sealed partial class CreateImportCommandHandler(
    TransferDbContext dbContext,
    TransferCatalog catalog,
    ArchiveStore store,
    ICurrentUser currentUser,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    IOptions<TransferOptions> options) : ICommandHandler<CreateImportCommand, TransferJobDto>
{
    public async ValueTask<TransferJobDto> Handle(CreateImportCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var limits = options.Value;
        string tenantId = TenantIds.Current(tenantAccessor);

        var temp = TempArchive.Create();
        await using (temp.ConfigureAwait(false))
        {
            // Count the bytes actually received: the declared length is the client's word.
            string sha = await TempArchive.CopyAsync(command.Content, temp, limits.MaxUploadBytes, cancellationToken).ConfigureAwait(false);
            long size = temp.Length;

            TransferManifest manifest;
            using (var archive = TransferArchive.Open(temp, limits))
            {
                manifest = archive.Manifest;
            }

            string[] entityTypes = [.. manifest.Files.Select(f => f.EntityType)];
            await catalog.DemandImportAsync(entityTypes, cancellationToken).ConfigureAwait(false);

            var jobId = Guid.CreateVersion7();
            temp.Seek(0, SeekOrigin.Begin);
            string key = await store.SaveAsync(tenantId, jobId, temp, cancellationToken).ConfigureAwait(false);

            var rows = manifest.Files.ToDictionary(f => f.EntityType, f => f.Rows, StringComparer.Ordinal);
            var job = TransferJob.CreateImport(jobId, entityTypes, key, SafeFileName(command.FileName), size, sha,
                new TransferSummary(rows, 0, 0, 0, 0, []), currentUser.GetUserId().ToString());
            dbContext.TransferJobs.Add(job);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return job.ToDto();
        }
    }

    /// <summary>The client's file name is display-only: last path segment, <c>[A-Za-z0-9._-]</c>, at most 100 characters.</summary>
    internal static string SafeFileName(string? fileName)
    {
        string name = (fileName ?? string.Empty).Replace('\\', '/');
        name = UnsafeChars().Replace(name[(name.LastIndexOf('/') + 1)..], "_").Trim('.', '_');
        if (name.Length > 100) name = name[^100..];
        return name.Length == 0 ? "import.zip" : name;
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeChars();
}
