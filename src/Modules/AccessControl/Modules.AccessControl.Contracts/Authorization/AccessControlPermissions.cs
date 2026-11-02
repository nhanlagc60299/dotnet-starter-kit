using FSH.Framework.Shared.Constants;

namespace FSH.Modules.AccessControl.Contracts.Authorization;

public static class AccessControlPermissions
{
    public static class AccessControl
    {
        public const string Resource = "AccessControl";
        public const string View     = $"Permissions.{Resource}.View";
        public const string Manage   = $"Permissions.{Resource}.Manage";
    }

    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View Access Grants",   ActionConstants.View, AccessControl.Resource),
        new("Manage Access Grants", "Manage",             AccessControl.Resource),
    ];
}
