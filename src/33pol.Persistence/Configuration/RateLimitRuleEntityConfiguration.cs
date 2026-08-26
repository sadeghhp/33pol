using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class RateLimitRuleEntityConfiguration : IEntityTypeConfiguration<RateLimitRuleEntity>
{
    public void Configure(EntityTypeBuilder<RateLimitRuleEntity> builder)
    {
        builder.ToTable("rate_limit_rules");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Scope)
            .HasMaxLength(32)
            .IsRequired();

        // NOCASE so "GPT-4" and "gpt-4" are one rule in the database as well as in the snapshot,
        // which compares its maps OrdinalIgnoreCase. Without it the unique index below would admit
        // both spellings and the snapshot would keep whichever the dictionary happened to load last.
        builder.Property(r => r.TargetKey)
            .HasMaxLength(256)
            .UseCollation("NOCASE")
            .IsRequired();

        builder.HasIndex(r => new { r.Scope, r.TargetKey }).IsUnique();
    }
}
