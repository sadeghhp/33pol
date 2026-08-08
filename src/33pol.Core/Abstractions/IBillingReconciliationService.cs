using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Compares the billing event ledger against the daily usage rollups derived from it.
/// </summary>
public interface IBillingReconciliationService
{
    /// <summary>
    /// Reconciles every bucket in the inclusive date window.
    /// </summary>
    /// <remarks>
    /// Callers should keep the window comfortably inside
    /// <see cref="Configuration.BillingOptions.UsageRetentionDays"/>: retention prunes the ledger but
    /// not the rollups, so a window reaching past it reports every pruned day as
    /// <see cref="BillingReconciliationKind.MissingFromEvents"/> — a true statement about the data
    /// that says nothing about correctness.
    /// </remarks>
    Task<BillingReconciliationReport> ReconcileAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default);
}
