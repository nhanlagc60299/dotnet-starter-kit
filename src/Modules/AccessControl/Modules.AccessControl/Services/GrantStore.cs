using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.AccessControl.Data;
using FSH.Modules.Identity.Contracts.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace FSH.Modules.AccessControl.Services;

/// <summary>A stored grant as cached per (tenant, role Id); <see cref="Condition"/> is the raw JSON (null = every row).</summary>
public sealed record CachedGrant(string Subject, string Action, string? Condition);

/// <summary>
/// The grants of a role in the current tenant, cached in <see cref="HybridCache"/> per (tenant, role Id) and evicted
/// by <see cref="InvalidateAsync"/> after every write, plus the tenant's role name → Id map that turns the JWT's
/// role names into Ids. Entries expire after one minute (<see cref="EntryOptions"/>).
/// </summary>
public sealed class GrantStore(
    AccessControlDbContext db,
    HybridCache cache,
    IRoleService roleService,
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

    public async Task<CachedGrant[]> GetAsync(string roleId, CancellationToken ct = default)
    {
        if (TenantId() is not { } tenantId) return [];
        return await cache.GetOrCreateAsync(
            Key(tenantId, roleId),
            (db, roleId),
            static async (state, token) => await state.db.AccessGrants.AsNoTracking()
                .Where(g => g.RoleId == state.roleId)
                .OrderBy(g => g.Subject).ThenBy(g => g.Action)
                .Select(g => new CachedGrant(g.Subject, g.Action, g.Condition))
                .ToArrayAsync(token)
                .ConfigureAwait(false),
            EntryOptions,
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The Ids of the roles with these names (JWT role claims) in the current tenant; names that no longer exist
    /// (renamed or deleted since the token was issued) are skipped, so they reach no grants.
    /// </summary>
    public async Task<List<string>> RoleIdsAsync(IEnumerable<string> roleNames, CancellationToken ct = default)
    {
        if (TenantId() is not { } tenantId) return [];
        var map = await cache.GetOrCreateAsync(
            RolesKey(tenantId),
            roleService,
            static async (roles, token) => await RoleNames.AllAsync(roles, token).ConfigureAwait(false),
            EntryOptions,
            cancellationToken: ct).ConfigureAwait(false);
        return [.. roleNames.Select(n => map.GetValueOrDefault(n)).OfType<string>()];
    }

    /// <summary>Evicts the grants of one role and the tenant's role map (a role created since it was cached).</summary>
    public async Task InvalidateAsync(string roleId, CancellationToken ct = default)
    {
        if (TenantId() is not { } tenantId) return;
        await cache.RemoveAsync(Key(tenantId, roleId), ct).ConfigureAwait(false);
        await cache.RemoveAsync(RolesKey(tenantId), ct).ConfigureAwait(false);
    }

    // Keys stay local: CacheKeys lives in BuildingBlocks, which this module must not modify.
    private static string Key(string tenantId, string roleId) => $"acl:t:{tenantId}:r:{roleId}";
    private static string RolesKey(string tenantId) => $"acl:t:{tenantId}:roles";

    private string? TenantId() => tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
}
