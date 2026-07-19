namespace Pol33.Core.Abstractions;

/// <summary>
/// Forces an immediate reload of the configuration snapshot from the database. Admin write paths
/// call this after committing a change so the new configuration is live in-process without waiting
/// for the reconcile poll. Concurrent calls collapse to a single reload (single-flight). Registered
/// only when a database is configured.
/// </summary>
public interface IGatewayConfigRefresher
{
    Task RefreshNowAsync(CancellationToken cancellationToken = default);
}
