using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class RecentRequestSnapshotEntityConfiguration : IEntityTypeConfiguration<RecentRequestSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<RecentRequestSnapshotEntity> builder)
    {
        builder.ToTable("recent_requests");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.RequestId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(r => r.Method)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(r => r.Path)
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(r => r.ModelId)
            .HasMaxLength(256);

        builder.Property(r => r.TenantId)
            .HasMaxLength(128);

        builder.Property(r => r.ErrorCode)
            .HasMaxLength(128);

        builder.Property(r => r.TimestampUtc)
            .HasColumnType("timestamptz");

        builder.HasIndex(r => r.TimestampUtc);
    }
}
