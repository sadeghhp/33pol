using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models.Overview;

namespace Pol33.Billing.Reconciliation;

/// <summary>
/// Holds the last reconciliation report where the Overview can read it. The sweep itself only logged
/// and published gauges, which left the console unable to say whether billing was balanced.
/// </summary>
public sealed class BillingReconciliationState : IBillingReconciliationStateSource
{
    private volatile ReconciliationStatus _current = new();

    public ReconciliationStatus Current => _current;

    public void MarkEnabled(bool enabled) => _current = _current with { Enabled = enabled };

    public void Record(BillingReconciliationReport report, DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        _current = new ReconciliationStatus
        {
            Enabled = true,
            LastRunUtc = completedAtUtc,
            FromDate = report.FromDate,
            ToDate = report.ToDate,
            BucketsCompared = report.BucketsCompared,
            DiscrepancyCount = report.Discrepancies.Count,
            AbsoluteCostDrift = report.AbsoluteCostDrift,
        };
    }
}
