using Mediator;

namespace FSH.Modules.Search.Contracts.v1.Documents;

/// <summary>
/// Adds or replaces the index entry for one entity of another module, keyed by
/// (<paramref name="EntityType"/>, <paramref name="EntityId"/>) within the current tenant.
/// </summary>
public sealed record IndexSearchDocumentCommand(
    string EntityType,
    string EntityId,
    string Title,
    string? Summary = null,
    string? Body = null,
    string? Url = null) : ICommand<Unit>;
