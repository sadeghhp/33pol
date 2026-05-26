using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class TenantEntityConfiguration : IEntityTypeConfiguration<TenantEntity>
{
    public void Configure(EntityTypeBuilder<TenantEntity> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Slug)
            .HasMaxLength(128)
            .IsRequired();

        builder.HasIndex(t => t.Slug)
            .IsUnique();

        builder.Property(t => t.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(t => t.PlanSlug)
            .HasMaxLength(64);

        builder.Property(t => t.CostCenter)
            .HasMaxLength(128);

        builder.Property(t => t.CreatedAt)
            .HasColumnType("timestamptz");

        builder.Property(t => t.UpdatedAt)
            .HasColumnType("timestamptz");

        builder.HasMany(t => t.ApiKeys)
            .WithOne(k => k.Tenant)
            .HasForeignKey(k => k.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(t => t.ModelGrants)
            .WithOne(g => g.Tenant)
            .HasForeignKey(g => g.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
