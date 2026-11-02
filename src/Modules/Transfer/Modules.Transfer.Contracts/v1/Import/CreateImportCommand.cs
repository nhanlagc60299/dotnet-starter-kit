using FSH.Modules.Transfer.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Transfer.Contracts.v1.Import;

/// <summary>
/// Validates an uploaded archive (zip limits, manifest, SHA-256, every NDJSON line), stores it privately and records a
/// Pending import job. Nothing is imported until <see cref="StartImportCommand"/>. <paramref name="FileName"/> is the
/// client's name and is only kept, sanitized, for display.
/// </summary>
public sealed record CreateImportCommand(Stream Content, string FileName, long Length) : ICommand<TransferJobDto>;
