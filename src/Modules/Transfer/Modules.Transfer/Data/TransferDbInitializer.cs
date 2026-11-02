using FSH.Framework.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.Transfer.Data;

public sealed class TransferDbInitializer(
    TransferDbContext dbContext,
    ILogger<TransferDbInitializer> logger) : IDbInitializer
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        if ((await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[Transfer] applied migrations");
        }
    }

    public Task SeedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
