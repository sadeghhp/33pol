using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class RateCardEntityConfiguration : IEntityTypeConfiguration<RateCardEntity>
{
    public void Configure(EntityTypeBuilder<RateCardEntity> builder)
    {
        builder.ToTable("rate_cards");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Slug)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasIndex(r => r.Slug)
            .IsUnique();

        builder.Property(r => r.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(r => r.ModelId)
            .HasMaxLength(256)
            .IsRequired();

        builder.HasIndex(r => new { r.ModelId, r.EffectiveFrom });

        builder.Property(r => r.InputPricePerMillionTokens)
            .HasPrecision(18, 6);

        builder.Property(r => r.OutputPricePerMillionTokens)
            .HasPrecision(18, 6);

        builder.Property(r => r.Currency)
            .HasMaxLength(3)
            .IsRequired();
    }
}
