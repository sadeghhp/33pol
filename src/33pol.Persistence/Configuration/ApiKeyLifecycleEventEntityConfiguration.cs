using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class ApiKeyLifecycleEventEntityConfiguration : IEntityTypeConfiguration<ApiKeyLifecycleEventEntity>
{
    public void Configure(EntityTypeBuilder<ApiKeyLifecycleEventEntity> builder)
    {
        builder.ToTable("api_key_lifecycle_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.KeyPrefix)
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(e => e.Label)
            .HasMaxLength(128);

        builder.Property(e => e.Event)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        // No HasOne/HasForeignKey to ApiKeyEntity: a deleted key's tombstone has to survive it.
        builder.HasIndex(e => new { e.ApiKeyId, e.OccurredAt });

        builder.HasIndex(e => new { e.TenantId, e.OccurredAt });
    }
}
