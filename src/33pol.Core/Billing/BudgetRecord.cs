namespace Pol33.Core.Billing;

public sealed record BudgetRecord(
    Guid Id,
    Guid TenantId,
    string Name,
    decimal AmountLimit,
    string Currency,
    decimal WarningThresholdRatio,
    bool HardStopEnabled,
    int PeriodStartDay,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
