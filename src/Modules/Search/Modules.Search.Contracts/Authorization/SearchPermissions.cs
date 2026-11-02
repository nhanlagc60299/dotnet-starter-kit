using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Search.Contracts.Authorization;

public static class SearchPermissions
{
    public static class Search
    {
        public const string Resource = "Search";
        public const string View     = $"Permissions.{Resource}.View";
        public const string Manage   = $"Permissions.{Resource}.Manage";
    }

    // View is not basic: a hit shows the title of whatever another module indexed, regardless of
    // the caller's permissions on that entity, so tenants grant it deliberately.
    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View Search",   ActionConstants.View, Search.Resource),
        new("Manage Search", "Manage",             Search.Resource),
    ];
}
