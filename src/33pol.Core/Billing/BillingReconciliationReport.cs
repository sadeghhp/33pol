namespace Pol33.Core.Billing;

/// <summary>Aggregate usage totals for one reconciliation bucket, or for a whole report.</summary>
public readonly record struct BillingReconciliationTotals(
    long PromptTokens,
    long CompletionTokens,
    decimal TotalCost,
    int RequestCount)
{
    public static readonly BillingReconciliationTotals Zero = new(0, 0, 0m, 0);

    public long TotalTokens => PromptTokens + CompletionTokens;

    public static BillingReconciliationTotals operator +(
        BillingReconciliationTotals left,
        BillingReconciliationTotals right) =>
        new(
            left.PromptTokens + right.PromptTokens,
            left.CompletionTokens + right.CompletionTokens,
            left.TotalCost + right.TotalCost,
            left.RequestCount + right.RequestCount);
}

/// <summary>How a bucket's ledger and rollup disagree.</summary>
public enum BillingReconciliationKind
{
    /// <summary>
    /// Billing events exist for the bucket but no rollup row does — spend that is recorded but
    /// invisible to every dashboard, budget check and daily summary, all of which read rollups.
    /// </summary>
    MissingFromRollups,

    /// <summary>
    /// A rollup row exists with no billing events behind it. Either the events were pruned by
    /// retention (check the window before treating this as a defect) or the rollup was written twice.
    /// </summary>
    MissingFromEvents,

    /// <summary>Both sides exist and their totals differ.</summary>
    TotalsDiffer,
}

/// <summary>One bucket whose stored rollup disagrees with the billing events behind it.</summary>
public sealed record BillingReconciliationDiscrepancy(
    BillingReconciliationKind Kind,
    DailyUsageRollupKey Key,
    BillingReconciliationTotals Events,
    BillingReconciliationTotals Rollup)
{
    /// <summary>Rollup minus events. Negative means the rollups under-report actual spend.</summary>
    public decimal CostDelta => Rollup.TotalCost - Events.TotalCost;

    public long TokenDelta => Rollup.TotalTokens - Events.TotalTokens;

    public int RequestCountDelta => Rollup.RequestCount - Events.RequestCount;
}

/// <summary>
/// The result of comparing the <c>billing_events</c> ledger against the <c>daily_usage_rollups</c>
/// derived from it.
/// </summary>
/// <remarks>
/// The ledger is the source of truth: it is the append-only, idempotent record written per request.
/// Rollups are a derived aggregate, and everything an operator actually looks at — the admin usage
/// pages, budget enforcement, the daily webhook — reads the aggregate, not the ledger. So any defect
/// between the two is invisible by construction: the numbers stay plausible and nothing errors. This
/// report is what makes that class of failure observable.
/// </remarks>
public sealed record BillingReconciliationReport(
    DateOnly FromDate,
    DateOnly ToDate,
    int BucketsCompared,
    BillingReconciliationTotals EventTotals,
    BillingReconciliationTotals RollupTotals,
    IReadOnlyList<BillingReconciliationDiscrepancy> Discrepancies)
{
    public static BillingReconciliationReport Empty(DateOnly fromDate, DateOnly toDate) =>
        new(fromDate, toDate, 0, BillingReconciliationTotals.Zero, BillingReconciliationTotals.Zero, []);

    public bool IsBalanced => Discrepancies.Count == 0;

    /// <summary>Rollup cost minus ledger cost across the window. Signed, so offsetting drift cancels.</summary>
    public decimal NetCostDrift => RollupTotals.TotalCost - EventTotals.TotalCost;

    /// <summary>
    /// Sum of the absolute per-bucket cost deltas. Unlike <see cref="NetCostDrift"/> this does not
    /// let an over-count in one bucket hide an under-count in another, so it is the figure to alert
    /// on.
    /// </summary>
    public decimal AbsoluteCostDrift => Discrepancies.Sum(d => Math.Abs(d.CostDelta));
}
