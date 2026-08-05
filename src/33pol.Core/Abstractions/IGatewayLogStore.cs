using Pol33.Core.Models;

namespace Pol33.Core.Abstractions;

/// <summary>
/// Bounded, in-process buffer of operator-facing diagnostics surfaced by the admin Logs tab.
/// Implementations must be safe to call from request threads and must never throw — a failure to
/// log is not worth failing the request that produced it.
/// </summary>
public interface IGatewayLogStore
{
    /// <summary>How many entries the buffer holds before the oldest are dropped.</summary>
    int Capacity { get; }

    void Record(GatewayLogEntry entry);

    /// <param name="minimumLevel">Drops entries below this severity. Null keeps all.</param>
    /// <param name="search">Case-insensitive substring matched against message, detail, category, model and code.</param>
    IReadOnlyList<GatewayLogEntry> GetRecent(int limit, GatewayLogLevel? minimumLevel = null, string? search = null);

    void Clear();
}
