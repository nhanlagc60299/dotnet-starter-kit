using FSH.Modules.Transfer.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Transfer.Contracts.v1.Export;

/// <summary>Queues an export of the given entity types of the current tenant into one archive.</summary>
public sealed record CreateExportCommand(IReadOnlyList<string> EntityTypes) : ICommand<TransferJobDto>;
