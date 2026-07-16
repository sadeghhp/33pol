using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Persists and restores per-partition monthly quota usage (see <see cref="QuotaUsageSnapshot"/>).
/// Registered only when a database connection string is configured.
/// </summary>
public interface IQuotaUsageSnapshotStore
{
    /// <summary>Loads all persisted usage rows.</summary>
    Task<IReadOnlyList<QuotaUsageSnapshot>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Upserts the supplied usage rows by partition key.</summary>
    Task SaveAsync(IReadOnlyList<QuotaUsageSnapshot> usages, CancellationToken cancellationToken = default);
}
