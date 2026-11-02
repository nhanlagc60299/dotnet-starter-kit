using System.Text.Json.Nodes;

namespace FSH.Modules.Transfer.Contracts;

/// <summary>
/// Exports one entity type of the current tenant as JSON rows (one NDJSON line each). Register it in the owning
/// module's DI as a scoped <see cref="ITransferSource"/>; the Transfer module discovers every registration.
/// </summary>
public interface ITransferSource
{
    /// <summary>Archive name of the entity type, e.g. <c>catalog.brands</c>: lowercase, <c>[a-z0-9]</c> words joined by <c>.</c>, <c>-</c> or <c>_</c>.</summary>
    string EntityType { get; }

    /// <summary>Permission the caller needs, on top of <c>Transfer.Export</c>, to export this type.</summary>
    string Permission { get; }

    /// <summary>The rows of the current tenant. Runs inside a tenant-scoped job, so the tenant query filter applies.</summary>
    IAsyncEnumerable<JsonObject> ExportAsync(CancellationToken cancellationToken);
}
