using FSH.Modules.AccessControl.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.AccessControl.Contracts.v1.Grants;

/// <summary>The grants of one role of the current tenant, ordered by subject then action.</summary>
public sealed record GetRoleGrantsQuery(string Role) : IQuery<IReadOnlyList<AccessGrantDto>>;
