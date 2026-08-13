using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

public enum GatewayErrorClearScope
{
    /// <summary>Error records, error counters, and failed rows in the Recent requests feed.</summary>
    Errors = 0,

    /// <summary>
    /// Also drops the persisted counter snapshot row outright, resetting total requests, latency
    /// and the whole recent feed with it.
    /// </summary>
    AllCounters = 1,
}

/// <summary>
/// Orchestrates clear-all across the in-memory counters, the error store and the persisted
/// snapshot.
/// </summary>
/// <remarks>
/// Implemented in the composition root because it is the only place allowed to see both the
/// observability layer and persistence — the admin endpoints may reference Core only.
/// <para>
/// Assumes a single gateway instance owns the counters, the same assumption the stats snapshot
/// service already documents. A second process against the same database would re-flush its own
/// totals after a clear.
/// </para>
/// </remarks>
public interface IGatewayErrorAdminService
{
    Task<GatewayErrorClearResult> ClearAllAsync(
        GatewayErrorClearScope scope,
        CancellationToken cancellationToken = default);
}
