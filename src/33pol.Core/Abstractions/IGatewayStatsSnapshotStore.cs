using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Persists and restores the dashboard runtime counters (see <see cref="GatewayRuntimeSnapshot"/>).
/// Registered only when a database connection string is configured.
/// </summary>
public interface IGatewayStatsSnapshotStore
{
    /// <summary>Loads the last persisted snapshot, or <c>null</c> if none has been written yet.</summary>
    Task<GatewayRuntimeSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the current absolute counter values, replacing the previous snapshot.</summary>
    Task SaveAsync(GatewayRuntimeSnapshot snapshot, CancellationToken cancellationToken = default);
}
