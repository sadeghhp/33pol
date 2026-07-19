using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class CorsSettingsEntityConfiguration : IEntityTypeConfiguration<CorsSettingsEntity>
{
    public void Configure(EntityTypeBuilder<CorsSettingsEntity> builder)
    {
        builder.ToTable("cors_settings");

        builder.HasKey(c => c.Id);

        // Single-row table; the key is assigned explicitly (always 1), never database-generated.
        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        // Mapped to a JSON TEXT column by EF's primitive-collection support on SQLite.
        builder.Property(c => c.AllowedOrigins)
            .IsRequired();
    }
}
