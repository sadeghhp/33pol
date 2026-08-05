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

        builder.Property(m => m.ModelId)
            .HasMaxLength(256)
            .IsRequired();

        builder.HasIndex(m => m.ModelId).IsUnique();

        builder.Property(m => m.Url)
            .IsRequired();

        builder.Property(m => m.ModelType)
            .HasMaxLength(64);
    }
}
