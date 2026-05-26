using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configurations;

internal sealed class TenantEntityConfiguration : IEntityTypeConfiguration<TenantEntity>
{
    public void Configure(EntityTypeBuilder<TenantEntity> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Slug).HasMaxLength(128).IsRequired();
        builder.HasIndex(t => t.Slug).IsUnique();
        builder.Property(t => t.Name).HasMaxLength(256).IsRequired();
        builder.Property(t => t.PlanSlug).HasMaxLength(64);
        builder.Property(t => t.CostCenter).HasMaxLength(128);
        builder.Property(t => t.CreatedAt).IsRequired();
        builder.Property(t => t.UpdatedAt).IsRequired();
    }
}
