using FSH.Framework.Core.Domain;
using FSH.Modules.Transfer.Contracts;
using FSH.Modules.Transfer.Contracts.Dtos;

namespace FSH.Modules.Transfer.Domain;

/// <summary>
/// One export or import of the current tenant. The archive lives in private storage at <see cref="StoragePath"/>.
/// Jobs and their archives are never deleted automatically.
/// </summary>
public sealed class TransferJob : BaseEntity<Guid>
{
    public TransferJobKind Kind { get; private set; }
    public TransferJobStatus Status { get; private set; }
    public IReadOnlyList<string> EntityTypes { get; private set; } = [];
#pragma warning disable S1144 // set by StartImport's atomic claim (ExecuteUpdate), which EF writes through this setter
    public TransferMode? Mode { get; private set; }
#pragma warning restore S1144
    public string? StoragePath { get; private set; }
    public string? FileName { get; private set; }
    public long SizeBytes { get; private set; }
    public string? Sha256 { get; private set; }
    public TransferSummary? Summary { get; private set; }
    public string? Error { get; private set; }
    public string CreatedBy { get; private set; } = default!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    private TransferJob() { }

    public static TransferJob CreateExport(IReadOnlyList<string> entityTypes, string createdBy) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = TransferJobKind.Export,
        Status = TransferJobStatus.Pending,
        EntityTypes = [.. entityTypes],
        CreatedBy = createdBy,
        CreatedAtUtc = DateTime.UtcNow,
    };

    /// <summary>An uploaded, validated archive waiting to be previewed or applied.</summary>
    public static TransferJob CreateImport(
        Guid id, IReadOnlyList<string> entityTypes, string storagePath, string fileName, long sizeBytes, string sha256,
        TransferSummary rows, string createdBy) => new()
    {
        Id = id,
        Kind = TransferJobKind.Import,
        Status = TransferJobStatus.Pending,
        EntityTypes = [.. entityTypes],
        StoragePath = storagePath,
        FileName = fileName,
        SizeBytes = sizeBytes,
        Sha256 = sha256,
        Summary = rows,
        CreatedBy = createdBy,
        CreatedAtUtc = DateTime.UtcNow,
    };

    public void MarkRunning()
    {
        Status = TransferJobStatus.Running;
        Error = null;
        CompletedAtUtc = null;
    }

    public void CompleteExport(string storagePath, string fileName, long sizeBytes, string sha256, TransferSummary summary)
    {
        StoragePath = storagePath;
        FileName = fileName;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        Complete(summary);
    }

    public void Complete(TransferSummary summary)
    {
        Summary = summary;
        Status = TransferJobStatus.Succeeded;
        CompletedAtUtc = DateTime.UtcNow;
    }

    public void Fail(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error.Length > 2000 ? error[..2000] : error;
        Status = TransferJobStatus.Failed;
        CompletedAtUtc = DateTime.UtcNow;
    }

    public TransferJobDto ToDto() => new(
        Id, Kind, Status, EntityTypes, Mode, FileName, SizeBytes, Sha256, Summary, Error, CreatedBy, CreatedAtUtc, CompletedAtUtc);
}
