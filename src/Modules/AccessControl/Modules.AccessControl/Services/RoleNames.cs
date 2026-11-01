using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;

namespace FSH.Modules.AccessControl.Services;

internal static class RoleNames
{
    /// <summary>
    /// The tenant role's name as Identity stores it (the value the JWT role claims carry), matched
    /// case-insensitively; 404 when the tenant has no such role.
    /// </summary>
    public static async Task<string> ResolveAsync(IRoleService roles, string role, CancellationToken ct)
    {
        var page = await roles.GetRolesAsync(1, 200, role, ct).ConfigureAwait(false);
        return page.Items.FirstOrDefault(r => string.Equals(r.Name, role, StringComparison.OrdinalIgnoreCase))?.Name
               ?? throw new NotFoundException("Role was not found.");
    }
}
