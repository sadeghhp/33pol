using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class QuotaUsageSnapshotEntityConfiguration : IEntityTypeConfiguration<QuotaUsageSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<QuotaUsageSnapshotEntity> builder)
    {
        builder.ToTable("quota_usage_snapshots");

        builder.HasKey(q => q.PartitionKey);

        builder.Property(q => q.PartitionKey)
            .HasMaxLength(512);

        builder.Property(q => q.Period)
            .HasMaxLength(7)
            .IsRequired();
    }
}
