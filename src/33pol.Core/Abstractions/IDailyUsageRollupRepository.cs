using Pol33.Core.Billing;

namespace Pol33.Core.Abstractions;

public interface IDailyUsageRollupRepository
{
    Task<IReadOnlyList<DailyUsageRollupRecord>> GetRollupsAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rollups visible under <paramref name="scope"/> for an inclusive date window, ordered by date
    /// then model. Unlike the tenant-only overload this can also return anonymous (no-tenant) rows.
    /// </summary>
    Task<IReadOnlyList<DailyUsageRollupRecord>> GetScopedRollupsAsync(
        UsageScope scope,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default);

    Task UpsertRollupsAsync(
        IReadOnlyList<DailyUsageRollupRecord> rollups,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies additive deltas to the matching rollup buckets, creating them if absent.
    /// </summary>
    /// <remarks>
    /// The increment is performed by the storage layer under a serializable transaction, so
    /// concurrent writers cannot lose one another's usage. Callers must NOT read a rollup, add to it
    /// in memory and write the absolute total back — that read-modify-write is exactly the pattern
    /// that dropped usage when two writers overlapped.
    /// </remarks>
    Task IncrementRollupsAsync(
        IReadOnlyList<DailyUsageRollupDelta> deltas,
        CancellationToken cancellationToken = default);
}
