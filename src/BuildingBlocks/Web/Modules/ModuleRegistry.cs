namespace FSH.Framework.Web.Modules;

/// <summary>
/// One row per module discovered at startup, with its resolved on/off state.
/// </summary>
/// <param name="Name">Module name — the module type name without the <c>Module</c> suffix (e.g. <c>Billing</c>).</param>
/// <param name="Enabled">Whether the module was loaded (services registered, endpoints mapped, migrations applied).</param>
/// <param name="Required">Whether the module can be switched off through configuration.</param>
public sealed record ModuleInfo(string Name, bool Enabled, bool Required);

/// <summary>
/// Read-only view of every module the host knows about and whether it is switched on.
/// Resolved once in <see cref="ModuleLoader.AddModules"/> from the <c>Modules:{Name}:Enabled</c> configuration keys.
/// </summary>
public interface IModuleRegistry
{
    IReadOnlyList<ModuleInfo> Modules { get; }

    bool IsEnabled(string moduleName);
}

internal sealed class ModuleRegistry : IModuleRegistry
{
    private readonly Dictionary<string, ModuleInfo> _byName;

    public ModuleRegistry(IReadOnlyList<ModuleInfo> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Modules = modules;
        _byName = modules.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ModuleInfo> Modules { get; }

    public bool IsEnabled(string moduleName)
        => _byName.TryGetValue(moduleName, out ModuleInfo? info) && info.Enabled;
}
