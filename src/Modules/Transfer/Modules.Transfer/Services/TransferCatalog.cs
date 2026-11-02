using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Transfer.Archive;
using FSH.Modules.Transfer.Contracts;
using FSH.Modules.Transfer.Contracts.Authorization;

namespace FSH.Modules.Transfer.Services;

/// <summary>
/// The sources and sinks other modules registered, by entity type (last registration wins), and the permission checks
/// for using them: <c>Transfer.Export</c>/<c>Transfer.Import</c> plus each type's own permission.
/// </summary>
public sealed class TransferCatalog(
    IEnumerable<ITransferSource> sources,
    IEnumerable<ITransferSink> sinks,
    ICurrentUser currentUser,
    IUserPermissionService permissions)
{
    public IEnumerable<string> EntityTypes() =>
        sources.Select(s => s.EntityType).Concat(sinks.Select(s => s.EntityType)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    public ITransferSource? FindSource(string entityType) => sources.LastOrDefault(s => s.EntityType == entityType);

    public ITransferSink? FindSink(string entityType) => sinks.LastOrDefault(s => s.EntityType == entityType);

    public ITransferSource Source(string entityType) =>
        FindSource(entityType) ?? throw TransferArchive.Invalid($"No module exports '{entityType}'.");

    public ITransferSink Sink(string entityType) =>
        FindSink(entityType) ?? throw TransferArchive.Invalid($"No module imports '{entityType}'.");

    public async Task<bool> HasAsync(string permission, CancellationToken cancellationToken)
    {
        Guid userId = currentUser.GetUserId();
        return userId != Guid.Empty
            && await permissions.HasPermissionAsync(userId.ToString(), permission, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>400 when a type has no source; 403 when the caller lacks <c>Transfer.Export</c> or a source's permission.</summary>
    public Task DemandExportAsync(IEnumerable<string> entityTypes, CancellationToken cancellationToken) =>
        DemandAsync([TransferPermissions.Transfer.Export, .. entityTypes.Select(t => Source(t).Permission)], cancellationToken);

    /// <summary>400 when a type has no sink; 403 when the caller lacks <c>Transfer.Import</c> or a sink's permission.</summary>
    public Task DemandImportAsync(IEnumerable<string> entityTypes, CancellationToken cancellationToken) =>
        DemandAsync([TransferPermissions.Transfer.Import, .. entityTypes.Select(t => Sink(t).Permission)], cancellationToken);

    private async Task DemandAsync(IEnumerable<string> required, CancellationToken cancellationToken)
    {
        foreach (string permission in required.Distinct(StringComparer.Ordinal))
        {
            if (!await HasAsync(permission, cancellationToken).ConfigureAwait(false))
            {
                throw new ForbiddenException($"You need the {permission} permission.");
            }
        }
    }
}
