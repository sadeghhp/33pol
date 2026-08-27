using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class ApiKeyEntityConfiguration : IEntityTypeConfiguration<ApiKeyEntity>
{
    public void Configure(EntityTypeBuilder<ApiKeyEntity> builder)
    {
        builder.ToTable("api_keys");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.KeyHash)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(k => k.KeyPrefix)
            .HasMaxLength(32)
            .IsRequired();

        builder.HasIndex(k => k.KeyPrefix);

        // The console's default view is "this tenant's keys that are not archived, newest first".
        // Without ArchivedAt in the index that listing degrades into a scan as keys accumulate.
        builder.HasIndex(k => new { k.TenantId, k.ArchivedAt, k.CreatedAt });

        builder.Property(k => k.Role)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(k => k.Label)
            .HasMaxLength(128);

        builder.Property(k => k.Assignee)
            .HasMaxLength(128);

        builder.Property(k => k.Description)
            .HasMaxLength(512);

        builder.Property(k => k.CostCenter)
            .HasMaxLength(128);
    }
}
