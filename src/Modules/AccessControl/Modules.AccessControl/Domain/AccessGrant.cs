using FSH.Framework.Core.Domain;

namespace FSH.Modules.AccessControl.Domain;

/// <summary>
/// Lets every holder of <see cref="Role"/> (an Identity role name, as carried by the JWT role claims) do
/// <see cref="Action"/> on <see cref="Subject"/>, limited to the rows matching <see cref="Condition"/>
/// (JSON, null = every row). Unique per (tenant, role, subject, action).
/// </summary>
public sealed class AccessGrant : BaseEntity<Guid>
{
    public string Role { get; private set; } = default!;
    public string Subject { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string? Condition { get; private set; }

    private AccessGrant() { }

    public static AccessGrant Create(string role, string subject, string action, string? condition) =>
        new() { Id = Guid.NewGuid(), Role = role, Subject = subject, Action = action, Condition = condition };

    public void SetCondition(string? condition) => Condition = condition;
}
