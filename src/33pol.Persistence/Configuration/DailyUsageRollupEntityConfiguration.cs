using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class DailyUsageRollupEntityConfiguration : IEntityTypeConfiguration<DailyUsageRollupEntity>
{
    public void Configure(EntityTypeBuilder<DailyUsageRollupEntity> builder)
    {
        builder.ToTable("daily_usage_rollups");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.ModelId)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(r => r.CostCenter)
            .HasMaxLength(128);

        builder.Property(r => r.TotalCost)
            .HasPrecision(18, 6);

        builder.Property(r => r.UpdatedAt)
            .HasColumnType("timestamptz");

        builder.HasIndex(r => new { r.UsageDate, r.TenantId, r.ModelId, r.CostCenter })
            .IsUnique();
    }
}
