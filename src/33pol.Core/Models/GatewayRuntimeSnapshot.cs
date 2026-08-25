namespace Pol33.Core.Models;

/// <summary>
/// A durable snapshot of the process-lifetime dashboard counters held in the in-memory runtime
/// state. Persisted to Postgres so the admin dashboard survives gateway container recreation.
/// Deliberately excludes uptime (should reset with the process) and active streams (a freshly
/// started process has none).
/// </summary>
public sealed record GatewayRuntimeSnapshot
{
    public long TotalRequests { get; init; }

    public long TotalErrors { get; init; }

    /// <summary>Requests whose client disconnected mid-response. Kept apart from <see cref="TotalErrors"/>.</summary>
    public long ClientDisconnects { get; init; }

    public long TotalLatencyMs { get; init; }

    public long RateLimitRejections { get; init; }

    public long QuotaRejections { get; init; }

    public IReadOnlyDictionary<string, long> RequestsPerModel { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, long> ErrorsPerModel { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Recent request feed ordered oldest-first.</summary>
    public IReadOnlyList<RecentRequestEntry> Recent { get; init; } = [];
}
