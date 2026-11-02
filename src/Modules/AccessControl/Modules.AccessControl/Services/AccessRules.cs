using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using FSH.Framework.Core.Context;
using FSH.Framework.Shared.Constants;

namespace FSH.Modules.AccessControl.Services;

internal static class AccessRules
{
    /// <summary>
    /// The caller's role names. FSH puts one <see cref="ClaimTypes.Role"/> claim per role name (direct and
    /// group roles) in the JWT, so grants are keyed by role name.
    /// </summary>
    public static List<string> Roles(ICurrentUser user) =>
        user.GetUserClaims()?.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList() ?? [];

    /// <summary>The tenant admin is FSH's built-in <see cref="RoleConstants.Admin"/> role (in every tenant).</summary>
    public static bool IsAdmin(IReadOnlyList<string> roles) => roles.Contains(RoleConstants.Admin, StringComparer.Ordinal);

    /// <summary>Whether a granted subject applies to a requested one: equal, or <c>name:*</c> for any <c>name:…</c>.</summary>
    public static bool SubjectCovers(string granted, string requested) =>
        string.Equals(granted, requested, StringComparison.Ordinal)
        || (granted.EndsWith(":*", StringComparison.Ordinal)
            && requested.StartsWith(granted[..^1], StringComparison.Ordinal));

    /// <summary>
    /// Escalation guard: a non-admin may only add, change or remove a grant when they themselves hold the same
    /// action on a covering subject <b>without a condition</b>. Returns the first grant that is not covered.
    /// </summary>
    public static CachedGrant? FirstUncovered(IReadOnlyList<CachedGrant> callerGrants, IEnumerable<CachedGrant> changed) =>
        changed.FirstOrDefault(g => !callerGrants.Any(mine =>
            mine.Condition is null
            && string.Equals(mine.Action, g.Action, StringComparison.Ordinal)
            && SubjectCovers(mine.Subject, g.Subject)));

    /// <summary>Grants present in one set but not the other, comparing conditions as JSON (key order and spacing ignored).</summary>
    public static List<CachedGrant> Changed(IReadOnlyList<CachedGrant> before, IReadOnlyList<CachedGrant> after) =>
        [.. after.Where(a => !before.Any(b => Same(a, b))), .. before.Where(b => !after.Any(a => Same(a, b)))];

    private static bool Same(CachedGrant a, CachedGrant b) =>
        a.Subject == b.Subject && a.Action == b.Action && JsonNode.DeepEquals(Parse(a.Condition), Parse(b.Condition));

    private static JsonNode? Parse(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json); // unparsable: compare as text
        }
    }
}
