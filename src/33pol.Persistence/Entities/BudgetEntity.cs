namespace Pol33.Persistence.Entities;

public sealed class BudgetEntity
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public decimal AmountLimit { get; set; }

    public required string Currency { get; set; }

    public decimal WarningThresholdRatio { get; set; } = 0.8m;

    public bool HardStopEnabled { get; set; }

    public int PeriodStartDay { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public TenantEntity? Tenant { get; set; }
}
