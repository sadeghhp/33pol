using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class QuotaSettingsEntityConfiguration : IEntityTypeConfiguration<QuotaSettingsEntity>
{
    public void Configure(EntityTypeBuilder<QuotaSettingsEntity> builder)
    {
        builder.ToTable("quota_settings");

        builder.HasKey(q => q.Id);

        // Single-row table; the key is assigned explicitly (always 1), never database-generated.
        builder.Property(q => q.Id)
            .ValueGeneratedNever();

        // SQLite stores decimal as TEXT (exact) by default; no HasColumnType needed.
        builder.Property(q => q.SoftLimitRatio)
            .IsRequired();
    }
}
