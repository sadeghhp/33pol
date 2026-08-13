using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configurations;

public sealed class GatewayErrorEntityConfiguration : IEntityTypeConfiguration<GatewayErrorEntity>
{
    public void Configure(EntityTypeBuilder<GatewayErrorEntity> builder)
    {
        builder.ToTable("gateway_errors");
        builder.HasKey(x => x.Id);

        builder.HasIndex(x => x.RecordId).IsUnique();

        // Every query is time-windowed, and pruning deletes by age, so this one carries the load.
        builder.HasIndex(x => x.OccurredAt);

        // The grouped view aggregates by fingerprint within a window, and drilling into a group
        // pages its occurrences newest-first.
        builder.HasIndex(x => new { x.Fingerprint, x.OccurredAt });

        builder.HasIndex(x => new { x.ModelId, x.OccurredAt });
        builder.HasIndex(x => new { x.StatusCode, x.OccurredAt });

        // "Show me everything that happened on this request" — the cross-link from Recent requests.
        builder.HasIndex(x => x.RequestId);

        builder.Property(x => x.RecordId).HasMaxLength(64);
        builder.Property(x => x.Fingerprint).HasMaxLength(32);
        builder.Property(x => x.Level).HasMaxLength(16);
        builder.Property(x => x.Source).HasMaxLength(24);
        builder.Property(x => x.Category).HasMaxLength(128);
        builder.Property(x => x.EventCode).HasMaxLength(64);
        builder.Property(x => x.Message).HasMaxLength(1000);
        builder.Property(x => x.ExceptionType).HasMaxLength(256);
        builder.Property(x => x.StackTrace).HasMaxLength(8000);
        builder.Property(x => x.Method).HasMaxLength(12);
        builder.Property(x => x.Path).HasMaxLength(512);
        builder.Property(x => x.RouteKind).HasMaxLength(32);
        builder.Property(x => x.ModelId).HasMaxLength(200);
        builder.Property(x => x.UpstreamTarget).HasMaxLength(512);
        builder.Property(x => x.Outcome).HasMaxLength(48);
        builder.Property(x => x.TenantId).HasMaxLength(64);
        builder.Property(x => x.ApiKeyId).HasMaxLength(64);
        builder.Property(x => x.RequestId).HasMaxLength(64);
        builder.Property(x => x.UpstreamBodySnippet).HasMaxLength(2048);
        builder.Property(x => x.Hint).HasMaxLength(512);
    }
}
