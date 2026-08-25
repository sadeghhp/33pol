using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class ModelRouteEntityConfiguration : IEntityTypeConfiguration<ModelRouteEntity>
{
    public void Configure(EntityTypeBuilder<ModelRouteEntity> builder)
    {
        builder.ToTable("model_routes");

        builder.HasKey(m => m.Id);

        // NOCASE: the registry resolves model ids case-insensitively, so "GPT-4o" and "gpt-4o"
        // must not be two routes (same fix as RateCardEntity.ModelId).
        builder.Property(m => m.ModelId)
            .HasMaxLength(256)
            .UseCollation("NOCASE")
            .IsRequired();

        builder.HasIndex(m => m.ModelId).IsUnique();

        builder.Property(m => m.Url)
            .IsRequired();

        builder.Property(m => m.ModelType)
            .HasMaxLength(64);

        builder.Property(m => m.State)
            .HasMaxLength(32)
            .HasDefaultValue("serving")
            .IsRequired();
    }
}
