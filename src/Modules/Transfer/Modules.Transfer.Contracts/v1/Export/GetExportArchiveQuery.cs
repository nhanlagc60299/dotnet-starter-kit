using FSH.Modules.Transfer.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Transfer.Contracts.v1.Export;

/// <summary>The archive of a succeeded export of the current tenant.</summary>
public sealed record GetExportArchiveQuery(Guid Id) : IQuery<TransferArchiveFile>;
