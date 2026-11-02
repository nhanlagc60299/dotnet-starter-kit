using FSH.Modules.Transfer.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Transfer.Contracts.v1.Jobs;

public sealed record GetTransferJobQuery(Guid Id) : IQuery<TransferJobDto>;
