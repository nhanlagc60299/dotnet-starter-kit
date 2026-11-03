using FSH.Framework.Core.Domain;

namespace FSH.Modules.AccessControl.Domain;

/// <summary>
/// Lets every holder of the Identity role <see cref="RoleId"/> do
/// <see cref="Action"/> on <see cref="Subject"/>, limited to the rows matching <see cref="Condition"/>
/// (JSON, null = every row). Unique per (tenant, role, subject, action). Keyed by role Id, not name, so the grants
/// follow a renamed role and are not inherited by a new role that reuses a deleted role's name.
/// </summary>
public sealed class AccessGrant : BaseEntity<Guid>
{
    public string RoleId { get; private set; } = default!;
    public string Subject { get; private set; } = default!;
    public string Action { get; private set; } = default!;
    public string? Condition { get; private set; }

    private AccessGrant() { }

    public static AccessGrant Create(string roleId, string subject, string action, string? condition) =>
        new() { Id = Guid.NewGuid(), RoleId = roleId, Subject = subject, Action = action, Condition = condition };

    public void SetCondition(string? condition) => Condition = condition;
}
