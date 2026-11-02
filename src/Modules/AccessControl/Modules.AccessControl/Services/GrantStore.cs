using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.AccessControl.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace FSH.Modules.AccessControl.Services;

/// <summary>A stored grant as cached per (tenant, role); <see cref="Condition"/> is the raw JSON (null = every row).</summary>
public sealed record CachedGrant(string Subject, string Action, string? Condition);

/// <summary>
/// The grants of a role in the current tenant, cached in <see cref="HybridCache"/> per (tenant, role) and evicted
/// by <see cref="InvalidateAsync"/> after every write; entries expire after one minute (<see cref="EntryOptions"/>).
/// </summary>
public sealed class GrantStore(
    AccessControlDbContext db,
    HybridCache cache,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
{
    /// <summary>
    /// One minute in both tiers bounds how long a revoked grant can survive: a read that overlaps a PUT can put the
    /// old set back after <see cref="InvalidateAsync"/>, and peers keep their L1 copy (no backplane). The cost is one
    /// small indexed query per (tenant, role) per node per minute.
    /// </summary>
    internal static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(1),
    };

    public async Task<CachedGrant[]> GetAsync(string role, CancellationToken ct = default)
    {
        if (TenantId() is not { } tenantId) return [];
        return await cache.GetOrCreateAsync(
            Key(tenantId, role),
            (db, role),
            static async (state, token) => await state.db.AccessGrants.AsNoTracking()
                .Where(g => g.Role == state.role)
                .OrderBy(g => g.Subject).ThenBy(g => g.Action)
                .Select(g => new CachedGrant(g.Subject, g.Action, g.Condition))
                .ToArrayAsync(token)
                .ConfigureAwait(false),
            EntryOptions,
            cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task InvalidateAsync(string role, CancellationToken ct = default)
    {
        if (TenantId() is { } tenantId)
            await cache.RemoveAsync(Key(tenantId, role), ct).ConfigureAwait(false);
    }

    // Keys stay local: CacheKeys lives in BuildingBlocks, which this module must not modify.
    private static string Key(string tenantId, string role) => $"acl:t:{tenantId}:r:{role}";

    private string? TenantId() => tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
}
