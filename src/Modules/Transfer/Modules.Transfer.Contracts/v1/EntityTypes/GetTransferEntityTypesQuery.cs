using FSH.Modules.Transfer.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Transfer.Contracts.v1.EntityTypes;

/// <summary>Every registered entity type, ordered by name, with what the caller may do with it.</summary>
public sealed record GetTransferEntityTypesQuery : IQuery<IReadOnlyList<TransferEntityTypeDto>>;
