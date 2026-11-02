using FSH.Modules.Transfer.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Transfer.Contracts.v1.Import;

/// <summary>Queues applying an uploaded archive (Pending or Failed import) with <paramref name="Mode"/>.</summary>
public sealed record StartImportCommand(Guid Id, TransferMode Mode) : ICommand<TransferJobDto>;
