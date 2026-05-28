using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configurations;

public sealed class QuotaUsageEntityConfiguration : IEntityTypeConfiguration<QuotaUsageEntity>
{
    public void Configure(EntityTypeBuilder<QuotaUsageEntity> builder)
    {
        builder.ToTable("quota_usages");
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => new { x.TenantId, x.PeriodStart })
            .IsUnique();
    }
}
