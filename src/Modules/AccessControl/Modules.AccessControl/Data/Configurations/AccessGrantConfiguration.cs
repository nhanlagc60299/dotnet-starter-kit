using FSH.Modules.AccessControl.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.AccessControl.Data.Configurations;

public sealed class AccessGrantConfiguration : IEntityTypeConfiguration<AccessGrant>
{
    public void Configure(EntityTypeBuilder<AccessGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("access_grants");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        // 256 = the Identity role name length.
        builder.Property(x => x.Role).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Subject).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Action).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Condition).HasColumnType("jsonb");

        // Widened to (Role, Subject, Action, TenantId) by BaseDbContext's tenant isolation.
        builder.HasIndex(x => new { x.Role, x.Subject, x.Action }).IsUnique();

        builder.Ignore(x => x.DomainEvents);
    }
}
