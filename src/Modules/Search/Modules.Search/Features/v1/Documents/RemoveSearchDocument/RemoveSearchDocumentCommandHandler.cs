using FSH.Modules.Search.Contracts.v1.Documents;
using FSH.Modules.Search.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Search.Features.v1.Documents.RemoveSearchDocument;

public sealed class RemoveSearchDocumentCommandHandler(SearchDbContext dbContext)
    : ICommandHandler<RemoveSearchDocumentCommand, Unit>
{
    public async ValueTask<Unit> Handle(RemoveSearchDocumentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var document = await dbContext.SearchDocuments
            .FirstOrDefaultAsync(d => d.EntityType == command.EntityType && d.EntityId == command.EntityId, cancellationToken)
            .ConfigureAwait(false);

        // Idempotent: the owner may remove an entry that was never indexed.
        if (document is not null)
        {
            dbContext.SearchDocuments.Remove(document);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        return Unit.Value;
    }
}
