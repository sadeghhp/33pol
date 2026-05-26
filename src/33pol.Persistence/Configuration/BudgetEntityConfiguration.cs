using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pol33.Persistence.Entities;

namespace Pol33.Persistence.Configuration;

internal sealed class BudgetEntityConfiguration : IEntityTypeConfiguration<BudgetEntity>
{
    public void Configure(EntityTypeBuilder<BudgetEntity> builder)
    {
        builder.ToTable("budgets");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Name)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(b => b.AmountLimit)
            .HasPrecision(18, 6);

        builder.Property(b => b.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(b => b.WarningThresholdRatio)
            .HasPrecision(5, 4);

        builder.Property(b => b.PeriodStartDay)
            .HasDefaultValue(1);

        builder.Property(b => b.CreatedAt)
            .HasColumnType("timestamptz");

        builder.Property(b => b.UpdatedAt)
            .HasColumnType("timestamptz");

        builder.HasIndex(b => b.TenantId);

        builder.HasOne(b => b.Tenant)
            .WithMany()
            .HasForeignKey(b => b.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
