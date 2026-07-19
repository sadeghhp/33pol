using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class GatewayStatsSnapshotEntityConfiguration : IEntityTypeConfiguration<GatewayStatsSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<GatewayStatsSnapshotEntity> builder)
    {
        builder.ToTable("gateway_stats_snapshot");

        builder.HasKey(s => s.Id);

        // The single-row key is assigned explicitly (always 1), never database-generated.
        builder.Property(s => s.Id)
            .ValueGeneratedNever();

        builder.Property(s => s.RequestsPerModelJson)
            .IsRequired();

        builder.Property(s => s.ErrorsPerModelJson)
            .IsRequired();
    }
}
