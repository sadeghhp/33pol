using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configurations;

internal sealed class ApiKeyEntityConfiguration : IEntityTypeConfiguration<ApiKeyEntity>
{
    public void Configure(EntityTypeBuilder<ApiKeyEntity> builder)
    {
        builder.ToTable("api_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.KeyHash).HasMaxLength(128).IsRequired();
        builder.HasIndex(k => k.KeyHash).IsUnique();
        builder.Property(k => k.KeyPrefix).HasMaxLength(32).IsRequired();
        builder.Property(k => k.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(k => k.Scopes)
            .HasConversion(
                scopes => System.Text.Json.JsonSerializer.Serialize(scopes),
                json => System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>());
        builder.Property(k => k.CreatedAt).IsRequired();
        builder.HasOne(k => k.Tenant)
            .WithMany(t => t.ApiKeys)
            .HasForeignKey(k => k.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
