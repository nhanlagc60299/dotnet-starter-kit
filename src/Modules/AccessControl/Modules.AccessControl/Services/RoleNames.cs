using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;

namespace FSH.Modules.AccessControl.Services;

internal static class RoleNames
{
    /// <summary>
    /// The Id of the tenant role with this name, matched case-insensitively; 404 when the tenant has no such role.
    /// Uncached: the grants API always sees Identity's current names.
    /// </summary>
    public static async Task<string> ResolveIdAsync(IRoleService roles, string role, CancellationToken ct)
    {
        var page = await roles.GetRolesAsync(1, 200, role, ct).ConfigureAwait(false);
        return page.Items.FirstOrDefault(r => string.Equals(r.Name, role, StringComparison.OrdinalIgnoreCase))?.Id
               ?? throw new NotFoundException("Role was not found.");
    }

    /// <summary>Every role of the current tenant, name → Id (names exactly as Identity stores them).</summary>
    public static async Task<Dictionary<string, string>> AllAsync(IRoleService roles, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int pages = 1;
        for (int page = 1; page <= pages; page++)
        {
            var result = await roles.GetRolesAsync(page, 200, null, ct).ConfigureAwait(false);
            foreach (var role in result.Items) map[role.Name] = role.Id;
            pages = result.TotalPages;
        }
        return map;
    }
}
