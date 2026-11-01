using Mediator;

namespace FSH.Modules.Search.Contracts.v1.Documents;

/// <summary>
/// Removes an index entry. The entry is derived data (the owning module keeps the real record),
/// so this is a hard delete; removing an entry that does not exist is a no-op.
/// </summary>
public sealed record RemoveSearchDocumentCommand(string EntityType, string EntityId) : ICommand<Unit>;
