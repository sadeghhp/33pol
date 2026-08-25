namespace Pol33.Core.Models.Overview;

/// <summary>The Overview's FinOps card: today / month-to-date / projected spend and the data-quality signals behind them.</summary>
public sealed record FinOpsOverview
{
    public DateTimeOffset BuiltAtUtc { get; init; }

    public string Currency { get; init; } = "USD";

    public decimal TodayCost { get; init; }

    public decimal YesterdayCost { get; init; }

    public decimal MonthToDateCost { get; init; }

    public decimal ProjectedMonthlyCost { get; init; }

    public decimal AverageDailyCost { get; init; }

    public long TodayRequests { get; init; }

    public long TodayPromptTokens { get; init; }

    public long TodayCompletionTokens { get; init; }

    public long MonthToDateRequests { get; init; }

    /// <summary>Registered models with no active rate card — their spend is recorded as zero.</summary>
    public IReadOnlyList<string> UnpricedModelIds { get; init; } = [];

    public int PricedModelCount { get; init; }

    public int RegisteredModelCount { get; init; }

    public IReadOnlyList<CostBreakdownRow> TopModelsMonthToDate { get; init; } = [];

    public IReadOnlyList<CostBreakdownRow> TopCostCentersMonthToDate { get; init; } = [];

    public ReconciliationStatus? Reconciliation { get; init; }

    public IReadOnlyList<BudgetStatus> Budgets { get; init; } = [];
}

public sealed record CostBreakdownRow(string Key, decimal Cost, long Requests);

public sealed record ReconciliationStatus
{
    public bool Enabled { get; init; }

    public DateTimeOffset? LastRunUtc { get; init; }

    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    public int BucketsCompared { get; init; }

    public int DiscrepancyCount { get; init; }

    public decimal AbsoluteCostDrift { get; init; }

    public bool IsBalanced => DiscrepancyCount == 0;
}

public sealed record BudgetStatus
{
    public Guid BudgetId { get; init; }

    public Guid TenantId { get; init; }

    public string? TenantSlug { get; init; }

    public required string Name { get; init; }

    public string Currency { get; init; } = "USD";

    public decimal Limit { get; init; }

    public decimal Spent { get; init; }

    /// <summary>Cost reserved by requests still in flight.</summary>
    public decimal Outstanding { get; init; }

    /// <summary>(Spent + Outstanding) ÷ Limit.</summary>
    public double Ratio { get; init; }

    public double WarningRatio { get; init; }

    public bool HardStopEnabled { get; init; }

    public DateOnly PeriodStart { get; init; }

    /// <summary>Day the budget is projected to be exhausted at the trailing average daily spend; null when not before period end.</summary>
    public DateOnly? ProjectedBreachDate { get; init; }
}
