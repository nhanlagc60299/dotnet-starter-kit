using FSH.Modules.Search.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Search.Data.Configurations;

public sealed class SearchDocumentConfiguration : IEntityTypeConfiguration<SearchDocument>
{
    public void Configure(EntityTypeBuilder<SearchDocument> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("search_documents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.EntityType).IsRequired().HasMaxLength(100);
        builder.Property(x => x.EntityId).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(500);
        builder.Property(x => x.Url).HasMaxLength(1000);

        // Widened to (EntityType, EntityId, TenantId) by BaseDbContext's tenant isolation.
        builder.HasIndex(x => new { x.EntityType, x.EntityId }).IsUnique();

        // The text search configuration is created by the module's initial migration (unaccent + simple).
        builder.Property(x => x.Tsv)
            .HasComputedColumnSql(
                $"setweight(to_tsvector('{SearchDbContext.TextSearchConfig}'::regconfig, coalesce(\"Title\", '')), 'A') || " +
                $"setweight(to_tsvector('{SearchDbContext.TextSearchConfig}'::regconfig, coalesce(\"Summary\", '')), 'B') || " +
                $"setweight(to_tsvector('{SearchDbContext.TextSearchConfig}'::regconfig, coalesce(\"Body\", '')), 'C')",
                stored: true);
        builder.HasIndex(x => x.Tsv).HasMethod("gin");

        builder.Ignore(x => x.DomainEvents);
    }
}
