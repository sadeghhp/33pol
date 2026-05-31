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

        builder.Property(k => k.Role)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(k => k.Scopes)
            .HasColumnType("jsonb");

        builder.Property(k => k.ExpiresAt)
            .HasColumnType("timestamptz");

        builder.Property(k => k.RevokedAt)
            .HasColumnType("timestamptz");

        builder.Property(k => k.CreatedAt)
            .HasColumnType("timestamptz");

        builder.Property(k => k.LastUsedAt)
            .HasColumnType("timestamptz");

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
