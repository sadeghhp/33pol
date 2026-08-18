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

        // TenantId and CostCenter are NOT NULL on purpose: SQLite treats NULLs as distinct in
        // UNIQUE indexes, so NULL-keyed buckets (anonymous traffic, no cost centre — the common
        // case) were not protected against duplicate rows. The mapper stores Guid.Empty / "" and
        // turns them back into null on read.
        builder.Property(r => r.TenantId)
            .IsRequired();

        builder.Property(r => r.CostCenter)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(r => r.TotalCost)
            .HasPrecision(18, 6);

        builder.HasIndex(r => new { r.UsageDate, r.TenantId, r.ModelId, r.CostCenter })
            .IsUnique();
    }
}
