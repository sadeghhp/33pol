using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class ApiKeyModelGrantEntityConfiguration : IEntityTypeConfiguration<ApiKeyModelGrantEntity>
{
    public void Configure(EntityTypeBuilder<ApiKeyModelGrantEntity> builder)
    {
        builder.ToTable("api_key_model_grants");

        builder.HasKey(g => g.Id);

        builder.Property(g => g.ModelPattern)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(g => g.Effect)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.HasIndex(g => new { g.ApiKeyId, g.ModelPattern }).IsUnique();

        builder.HasOne(g => g.ApiKey)
            .WithMany()
            .HasForeignKey(g => g.ApiKeyId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
