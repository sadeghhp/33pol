using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class ConfigVersionEntityConfiguration : IEntityTypeConfiguration<ConfigVersionEntity>
{
    public void Configure(EntityTypeBuilder<ConfigVersionEntity> builder)
    {
        builder.ToTable("config_version");

        builder.HasKey(c => c.Id);

        // Single-row table; the key is assigned explicitly (always 1), never database-generated.
        builder.Property(c => c.Id)
            .ValueGeneratedNever();

        builder.Property(c => c.Version)
            .IsRequired();
    }
}
