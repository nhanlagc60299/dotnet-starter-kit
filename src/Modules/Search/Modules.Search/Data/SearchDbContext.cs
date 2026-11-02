using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Persistence.Context;
using FSH.Framework.Shared.Multitenancy;
using FSH.Framework.Shared.Persistence;
using FSH.Modules.Search.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FSH.Modules.Search.Data;

public sealed class SearchDbContext : BaseDbContext
{
    public const string Schema = "search";

    /// <summary>Text search configuration: a copy of <c>simple</c> that unaccents words first, so "Đà Nẵng" matches "da nang".</summary>
    public const string TextSearchConfig = "search.vi";

    public SearchDbContext(
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        DbContextOptions<SearchDbContext> options,
        IOptions<DatabaseOptions> settings,
        IHostEnvironment environment) : base(multiTenantContextAccessor, options, settings, environment) { }

    public DbSet<SearchDocument> SearchDocuments => Set<SearchDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SearchDbContext).Assembly);
        // base.OnModelCreating runs LAST so the tenant filter and unique-index widening see the configured entity.
        base.OnModelCreating(modelBuilder);
    }
}
