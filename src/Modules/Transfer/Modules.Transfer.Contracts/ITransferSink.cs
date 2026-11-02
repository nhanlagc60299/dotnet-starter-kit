using System.Text.Json.Nodes;

namespace FSH.Modules.Transfer.Contracts;

/// <summary>
/// Imports rows of one entity type into the current tenant. There is no delete or mirror mode: an import only ever
/// creates rows or updates the ones it matches. Rows the sink cannot use are counted as failed, never thrown.
/// </summary>
public interface ITransferSink
{
    /// <summary>Archive name of the entity type; must match the exporting <see cref="ITransferSource.EntityType"/>.</summary>
    string EntityType { get; }

    /// <summary>Permission the caller needs, on top of <c>Transfer.Import</c>, to preview or apply this type.</summary>
    string Permission { get; }

    /// <summary>What <see cref="ApplyAsync"/> would do, without writing anything.</summary>
    ValueTask<TransferPreview> PreviewAsync(IAsyncEnumerable<JsonObject> rows, TransferMode mode, CancellationToken cancellationToken);

    ValueTask<TransferResult> ApplyAsync(IAsyncEnumerable<JsonObject> rows, TransferMode mode, CancellationToken cancellationToken);
}
