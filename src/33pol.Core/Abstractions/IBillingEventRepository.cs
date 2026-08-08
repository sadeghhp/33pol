using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public interface IBillingEventRepository
{
    Task<bool> TryAppendAsync(BillingEventRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillingEventRecord>> QueryAsync(
        BillingEventQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, ApiKeyUsageSummary>> GetUsageSummariesAsync(
        Guid tenantId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregates the ledger into the same buckets the daily rollups use — (date, tenant, model,
    /// cost centre) — over an inclusive date window.
    /// </summary>
    /// <remarks>
    /// This is the ledger side of reconciliation, so it must aggregate exactly the way the rollup
    /// writer does or every bucket reports a false discrepancy. Cost is summed in memory rather than
    /// by SQL: SQLite has no decimal type and coerces the TEXT-stored values to IEEE-754 doubles for
    /// a server-side SUM(), which is precisely the drift this method exists to detect.
    /// </remarks>
    Task<IReadOnlyList<DailyUsageRollupRecord>> GetDailyTotalsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken = default);
}
