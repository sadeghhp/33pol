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

    /// <summary>
    /// Aggregates the ledger rows matching <paramref name="filter"/> into daily rollup buckets
    /// (date, tenant, model, cost centre), exactly as <see cref="GetDailyTotalsAsync"/> does but
    /// honouring the tenant scope, key and cost-centre filters. <c>Limit</c> and <c>Cursor</c> are
    /// ignored. This is what backs per-key usage reports, which the rollup table cannot answer.
    /// </summary>
    Task<IReadOnlyList<DailyUsageRollupRecord>> AggregateDailyAsync(
        BillingEventQuery filter,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional batch contract for <see cref="IBillingEventRepository"/> implementations that can
/// append many events in one transaction. The usage writer prefers it when present and falls back
/// to <see cref="IBillingEventRepository.TryAppendAsync"/> per event otherwise.
/// </summary>
/// <remarks>
/// Kept separate from the repository interface so a "batch" that still commits row by row is never
/// silently accepted as batching: an implementation either provides one probe and one commit per
/// batch, or it does not claim to.
/// </remarks>
public interface IBillingEventBatchAppender
{
    /// <summary>
    /// Appends <paramref name="records"/> idempotently and returns the subset that was actually
    /// inserted (in input order). Records whose <c>RequestId</c> already exists are skipped.
    /// </summary>
    Task<IReadOnlyList<BillingEventRecord>> TryAppendManyAsync(
        IReadOnlyList<BillingEventRecord> records,
        CancellationToken cancellationToken = default);
}
