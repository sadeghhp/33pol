using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class RateLimitDefaultsEntityConfiguration : IEntityTypeConfiguration<RateLimitDefaultsEntity>
{
    public void Configure(EntityTypeBuilder<RateLimitDefaultsEntity> builder)
    {
        builder.ToTable("rate_limit_defaults");

        builder.HasKey(d => d.Id);

        // Single-row table; the key is assigned explicitly (always 1), never database-generated.
        builder.Property(d => d.Id)
            .ValueGeneratedNever();
    }
}
