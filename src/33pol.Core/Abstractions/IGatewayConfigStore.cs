using Pol33.Core.Configuration;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Loads the configuration snapshot from the database and manages the monotonic config version.
/// Registered only when a database connection string is configured.
/// </summary>
public interface IGatewayConfigStore
{
    /// <summary>Reads every config section plus the current version into an immutable snapshot.</summary>
    Task<GatewayConfigSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads just the current config version — a cheap check for the reconcile poll.</summary>
    Task<long> GetVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments and returns the config version. Admin write paths call this within the
    /// same transaction as their change so a version bump signals "something changed".
    /// </summary>
    Task<long> IncrementVersionAsync(CancellationToken cancellationToken = default);
}
