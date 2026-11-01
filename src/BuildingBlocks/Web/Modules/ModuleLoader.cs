using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Reflection;

namespace FSH.Framework.Web.Modules;

public static class ModuleLoader
{
    /// <summary>Configuration section that switches modules on and off: <c>Modules:{Name}:Enabled</c>.</summary>
    public const string ConfigurationSection = "Modules";

    private const string ModuleSuffix = "Module";

    private static readonly List<IModule> _modules = new();
    private static readonly object _lock = new();
    private static bool _modulesLoaded;

    /// <summary>
    /// Discovers every <see cref="IModule"/> declared through <see cref="FshModuleAttribute"/> in the given assemblies and loads
    /// the ones switched on. A module is off when <c>Modules:{Name}:Enabled</c> is <c>false</c>; a missing key means on.
    /// <para>
    /// A module that is off contributes nothing to the host: its <see cref="IModule.ConfigureServices"/> never runs, its
    /// validators are not scanned, its endpoints and middleware are not mapped, and every service descriptor whose
    /// implementation lives in its assembly (Mediator-generated handlers, for instance) is removed so DI validation stays green.
    /// Modules marked <see cref="FshModuleAttribute.Required"/> ignore the switch.
    /// </para>
    /// </summary>
    public static IHostApplicationBuilder AddModules(this IHostApplicationBuilder builder, params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(builder);

        lock (_lock)
        {
            if (_modulesLoaded)
            {
                return builder;
            }

            var source = assemblies is { Length: > 0 }
                ? assemblies
                : AppDomain.CurrentDomain.GetAssemblies();

            IConfigurationSection switches = builder.Configuration.GetSection(ConfigurationSection);

            var registrations = source
                .SelectMany(a => a.GetCustomAttributes<FshModuleAttribute>())
                .Where(r => typeof(IModule).IsAssignableFrom(r.ModuleType))
                .DistinctBy(r => r.ModuleType)
                .OrderBy(r => r.Order)
                .ThenBy(r => r.ModuleType.Name)
                .ToList();

            var infos = new List<ModuleInfo>(registrations.Count);
            var disabledAssemblies = new HashSet<Assembly>();

            foreach (FshModuleAttribute registration in registrations)
            {
                string name = ModuleName(registration.ModuleType);
                bool enabled = registration.Required || IsSwitchedOn(switches, name);
                infos.Add(new ModuleInfo(name, enabled, registration.Required));

                if (!enabled)
                {
                    disabledAssemblies.Add(registration.ModuleType.Assembly);
                }
            }

            Assembly[] enabledAssemblies = source
                .Where(a => !disabledAssemblies.Contains(a))
                .ToArray();

            builder.Services.AddValidatorsFromAssemblies(enabledAssemblies);

            IEnumerable<Type> enabledModuleTypes = registrations
                .Where(r => !disabledAssemblies.Contains(r.ModuleType.Assembly))
                .Select(r => r.ModuleType);

            foreach (Type moduleType in enabledModuleTypes)
            {
                if (Activator.CreateInstance(moduleType) is not IModule module)
                {
                    throw new InvalidOperationException($"Unable to create module {moduleType.Name}.");
                }

                module.ConfigureServices(builder);
                _modules.Add(module);
            }

            if (disabledAssemblies.Count > 0)
            {
                RemoveServicesFrom(builder.Services, disabledAssemblies);
            }

            builder.Services.AddSingleton<IModuleRegistry>(new ModuleRegistry(infos.AsReadOnly()));

            _modulesLoaded = true;
        }

        return builder;
    }

    public static IApplicationBuilder UseModuleMiddlewares(this IApplicationBuilder app)
    {
        foreach (var m in _modules)
            m.ConfigureMiddleware(app);

        return app;
    }

    public static IEndpointRouteBuilder MapModules(this IEndpointRouteBuilder endpoints)
    {
        foreach (var m in _modules)
            m.MapEndpoints(endpoints);

        return endpoints;
    }

    /// <summary>Module name used in configuration and in <see cref="IModuleRegistry"/>: the type name without the <c>Module</c> suffix.</summary>
    public static string ModuleName(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);

        string name = moduleType.Name;
        return name.EndsWith(ModuleSuffix, StringComparison.Ordinal) && name.Length > ModuleSuffix.Length
            ? name[..^ModuleSuffix.Length]
            : name;
    }

    private static bool IsSwitchedOn(IConfigurationSection switches, string moduleName)
    {
        string? raw = switches.GetSection(moduleName)["Enabled"];
        return raw is null || !bool.TryParse(raw, out bool enabled) || enabled;
    }

    /// <summary>
    /// Drops every descriptor whose implementation type comes from a switched-off module assembly. Mediator's source
    /// generator registers handlers before <see cref="AddModules"/> runs, and a handler whose DbContext was never
    /// registered would fail build-time DI validation in Development.
    /// </summary>
    private static void RemoveServicesFrom(IServiceCollection services, HashSet<Assembly> assemblies)
    {
        for (int i = services.Count - 1; i >= 0; i--)
        {
            ServiceDescriptor descriptor = services[i];
            Type? implementation = descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();

            if (implementation is not null && assemblies.Contains(implementation.Assembly))
            {
                services.RemoveAt(i);
            }
        }
    }
}
