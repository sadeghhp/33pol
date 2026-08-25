using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class MaintenanceStateEntityConfiguration : IEntityTypeConfiguration<MaintenanceStateEntity>
{
    public void Configure(EntityTypeBuilder<MaintenanceStateEntity> builder)
    {
        builder.ToTable("maintenance_state");

        builder.HasKey(m => m.Key);

        builder.Property(m => m.Key)
            .HasMaxLength(128);

        builder.Property(m => m.ValueJson)
            .IsRequired();
    }
}
