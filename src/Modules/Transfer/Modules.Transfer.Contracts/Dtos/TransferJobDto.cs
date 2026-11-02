namespace FSH.Modules.Transfer.Contracts.Dtos;

public enum TransferJobKind
{
    Export,
    Import,
}

/// <summary>Pending: export queued, or import uploaded and not applied yet. Running: export or apply queued or running.</summary>
public enum TransferJobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>
/// Row counts of a job: <see cref="Rows"/> per entity type in the archive; the create/update/skip/fail counts and
/// the first <see cref="TransferTally.MaxErrors"/> errors of an import (zero for an export).
/// </summary>
public sealed record TransferSummary(
    IReadOnlyDictionary<string, int> Rows,
    int Created,
    int Updated,
    int Skipped,
    int Failed,
    IReadOnlyList<TransferRowError> Errors);

public sealed record TransferJobDto(
    Guid Id,
    TransferJobKind Kind,
    TransferJobStatus Status,
    IReadOnlyList<string> EntityTypes,
    TransferMode? Mode,
    string? FileName,
    long SizeBytes,
    string? Sha256,
    TransferSummary? Summary,
    string? Error,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc);

/// <summary>An entity type some module registered, and whether the caller may export/import it.</summary>
public sealed record TransferEntityTypeDto(string EntityType, bool CanExport, bool CanImport);

/// <summary>An export archive to stream. The caller disposes <see cref="Content"/>.</summary>
public sealed record TransferArchiveFile(Stream Content, string FileName, long? Length);
