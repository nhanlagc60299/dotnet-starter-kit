using System;

namespace FSH.Framework.Web.Modules;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class FshModuleAttribute : Attribute
{
    public Type ModuleType { get; }

    /// <summary>
    /// Optional ordering hint that allows hosts to control module startup sequencing.
    /// Lower numbers execute first.
    /// </summary>
    public int Order { get; }

    /// <summary>
    /// A required module ignores <c>Modules:{Name}:Enabled=false</c> and always loads.
    /// Use it for modules the host cannot run without (Identity, Multitenancy).
    /// </summary>
    public bool Required { get; init; }

    public FshModuleAttribute(Type moduleType, int order = 0)
    {
        ModuleType = moduleType ?? throw new ArgumentNullException(nameof(moduleType));
        Order = order;
    }
}
