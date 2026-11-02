using FSH.Modules.Transfer.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Transfer.Contracts.v1.Import;

/// <summary>What applying the uploaded archive with <paramref name="Mode"/> would do; writes nothing.</summary>
public sealed record GetImportPreviewQuery(Guid Id, TransferMode Mode) : IQuery<TransferSummary>;

/// <summary>Body of the preview and apply endpoints.</summary>
public sealed record ImportModeRequest(TransferMode Mode);
