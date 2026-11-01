using FSH.Modules.AccessControl.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.AccessControl.Contracts.v1.Grants;

/// <summary>
/// Replaces the whole grant set of a role. A caller who is not a tenant Admin may only add, change or remove
/// grants for (subject, action) pairs they hold themselves without a condition.
/// </summary>
public sealed record UpdateRoleGrantsCommand(string Role, IReadOnlyList<AccessGrantDto> Grants) : ICommand<Unit>;
