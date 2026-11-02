using FSH.Modules.Transfer.Contracts.Authorization;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Contracts.v1.EntityTypes;
using FSH.Modules.Transfer.Services;
using Mediator;

namespace FSH.Modules.Transfer.Features.v1.EntityTypes.GetTransferEntityTypes;

public sealed class GetTransferEntityTypesQueryHandler(TransferCatalog catalog)
    : IQueryHandler<GetTransferEntityTypesQuery, IReadOnlyList<TransferEntityTypeDto>>
{
    public async ValueTask<IReadOnlyList<TransferEntityTypeDto>> Handle(GetTransferEntityTypesQuery query, CancellationToken cancellationToken)
    {
        bool export = await catalog.HasAsync(TransferPermissions.Transfer.Export, cancellationToken).ConfigureAwait(false);
        bool import = await catalog.HasAsync(TransferPermissions.Transfer.Import, cancellationToken).ConfigureAwait(false);

        var result = new List<TransferEntityTypeDto>();
        foreach (string entityType in catalog.EntityTypes())
        {
            var source = catalog.FindSource(entityType);
            var sink = catalog.FindSink(entityType);
            bool canExport = export && source is not null
                && await catalog.HasAsync(source.Permission, cancellationToken).ConfigureAwait(false);
            bool canImport = import && sink is not null
                && await catalog.HasAsync(sink.Permission, cancellationToken).ConfigureAwait(false);
            result.Add(new TransferEntityTypeDto(entityType, canExport, canImport));
        }

        return result;
    }
}
