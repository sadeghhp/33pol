using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class RateLimitPlanEntityConfiguration : IEntityTypeConfiguration<RateLimitPlanEntity>
{
    public void Configure(EntityTypeBuilder<RateLimitPlanEntity> builder)
    {
        builder.ToTable("rate_limit_plans");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Slug)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(p => p.Slug).IsUnique();
    }
}
