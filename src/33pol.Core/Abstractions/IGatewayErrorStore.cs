using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

/// <summary>
/// The write half of error tracking, separated so the request path depends only on what it needs.
/// </summary>
/// <remarks>
/// Deliberately synchronous and void-returning: this is called from the middleware that is about
/// to answer a caller. Implementations must never block on I/O and must never throw — failing to
/// record a failure is not worth compounding it.
/// </remarks>
public interface IGatewayErrorRecorder
{
    /// <summary>
    /// Records one occurrence. The implementation computes the fingerprint and applies redaction;
    /// callers pass the raw record.
    /// </summary>
    void Record(GatewayErrorRecord record);
}

/// <summary>Read and administer recorded errors. Backed by the database when one is configured.</summary>
public interface IGatewayErrorStore : IGatewayErrorRecorder
{
    /// <summary>False when errors live only in memory and will not survive a restart.</summary>
    bool IsPersistent { get; }

    Task<GatewayErrorGroupPage> QueryGroupsAsync(GatewayErrorQuery query, CancellationToken cancellationToken = default);

    Task<GatewayErrorPage> QueryAsync(GatewayErrorQuery query, CancellationToken cancellationToken = default);

    Task<GatewayErrorRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<GatewayErrorFacets> GetFacetsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every record. Returns how many were removed.</summary>
    Task<int> ClearAsync(CancellationToken cancellationToken = default);
}
