using FSH.Modules.Search.Contracts.v1.Documents;
using FSH.Modules.Search.Data;
using FSH.Modules.Search.Domain;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Search.Features.v1.Documents.IndexSearchDocument;

public sealed class IndexSearchDocumentCommandHandler(SearchDbContext dbContext)
    : ICommandHandler<IndexSearchDocumentCommand, Unit>
{
    public async ValueTask<Unit> Handle(IndexSearchDocumentCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var document = await dbContext.SearchDocuments
            .FirstOrDefaultAsync(d => d.EntityType == command.EntityType && d.EntityId == command.EntityId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            dbContext.SearchDocuments.Add(SearchDocument.Create(
                command.EntityType, command.EntityId, command.Title, command.Summary, command.Body, command.Url));
        }
        else
        {
            document.Update(command.Title, command.Summary, command.Body, command.Url);
        }

        // ponytail: two concurrent first-time indexes of the same entity race to the unique index and one
        // fails (retry is safe); switch to INSERT ... ON CONFLICT if callers index the same entity in parallel.
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
