using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configurations;

public sealed class QuotaAllocationEntityConfiguration : IEntityTypeConfiguration<QuotaAllocationEntity>
{
    public void Configure(EntityTypeBuilder<QuotaAllocationEntity> builder)
    {
        builder.ToTable("quota_allocations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SoftLimitRatio)
            .HasPrecision(5, 4);

        builder.HasIndex(x => new { x.TenantId, x.PeriodStart, x.PeriodEnd })
            .IsUnique();
    }
}
