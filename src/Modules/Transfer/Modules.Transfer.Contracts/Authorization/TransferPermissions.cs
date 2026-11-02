using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Transfer.Contracts.Authorization;

public static class TransferPermissions
{
    public static class Transfer
    {
        public const string Resource = "Transfer";
        public const string Export   = $"Permissions.{Resource}.Export";
        public const string Import   = $"Permissions.{Resource}.Import";
    }

    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("Export Data", "Export", Transfer.Resource),
        new("Import Data", "Import", Transfer.Resource),
    ];
}
