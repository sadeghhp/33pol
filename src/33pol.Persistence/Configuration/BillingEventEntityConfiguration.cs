using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class BillingEventEntityConfiguration : IEntityTypeConfiguration<BillingEventEntity>
{
    public void Configure(EntityTypeBuilder<BillingEventEntity> builder)
    {
        builder.ToTable("billing_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.RequestId)
            .HasMaxLength(128)
            .IsRequired();

        builder.HasIndex(e => e.RequestId)
            .IsUnique();

        builder.Property(e => e.ModelId)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(e => e.CostCenter)
            .HasMaxLength(128);

        builder.Property(e => e.InputCost)
            .HasPrecision(18, 6);

        builder.Property(e => e.OutputCost)
            .HasPrecision(18, 6);

        builder.Property(e => e.TotalCost)
            .HasPrecision(18, 6);

        builder.HasIndex(e => new { e.TenantId, e.RecordedAt });

        builder.HasIndex(e => new { e.ApiKeyId, e.RecordedAt });

        builder.HasIndex(e => new { e.CostCenter, e.RecordedAt });
    }
}
