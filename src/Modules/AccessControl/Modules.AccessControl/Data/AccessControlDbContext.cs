using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Persistence.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.AccessControl.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FSH.Modules.AccessControl.Data;

public sealed class AccessControlDbContext : BaseDbContext
{
    public const string Schema = "access";

    public AccessControlDbContext(
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        DbContextOptions<AccessControlDbContext> options,
        IOptions<DatabaseOptions> settings,
        IHostEnvironment environment) : base(multiTenantContextAccessor, options, settings, environment) { }

    public DbSet<AccessGrant> AccessGrants => Set<AccessGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccessControlDbContext).Assembly);
        // base.OnModelCreating runs LAST so the tenant filter and unique-index widening see the configured entity.
        base.OnModelCreating(modelBuilder);
    }
}
