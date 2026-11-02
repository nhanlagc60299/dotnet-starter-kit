using System.Text.Json;
using FSH.Modules.Transfer.Contracts.Dtos;
using FSH.Modules.Transfer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FSH.Modules.Transfer.Data.Configurations;

public sealed class TransferJobConfiguration : IEntityTypeConfiguration<TransferJob>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<TransferJob> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ToTable("transfer_jobs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);

        builder.Property(x => x.EntityTypes)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<string[]>(v, Json) ?? Array.Empty<string>(),
                new ValueComparer<IReadOnlyList<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode(StringComparison.Ordinal))),
                    v => v.ToArray()));

        builder.Property(x => x.Summary)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<TransferSummary>(v, Json));

        builder.Property(x => x.StoragePath).HasMaxLength(512);
        builder.Property(x => x.FileName).HasMaxLength(255);
        builder.Property(x => x.Sha256).HasMaxLength(64);
        builder.Property(x => x.Error).HasMaxLength(2000);
        builder.Property(x => x.CreatedBy).IsRequired().HasMaxLength(64);

        builder.Ignore(x => x.DomainEvents);
    }
}
