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

        // Identity role Ids are Guid strings; 450 is ASP.NET Identity's key length.
        builder.Property(x => x.RoleId).IsRequired().HasMaxLength(450);
        builder.Property(x => x.Subject).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Action).IsRequired().HasMaxLength(50);
        builder.Property(x => x.Condition).HasColumnType("jsonb");

        // Widened to (RoleId, Subject, Action, TenantId) by BaseDbContext's tenant isolation.
        builder.HasIndex(x => new { x.RoleId, x.Subject, x.Action }).IsUnique();

        builder.Ignore(x => x.DomainEvents);
    }
}
