namespace Pol33.Core.Models;

public sealed class AdminSummarySnapshot
{
    public required string Uptime { get; init; }

    public long UptimeSeconds { get; init; }

    public long TotalInferenceRequests { get; init; }

    public long TotalErrors { get; init; }

    public double AverageLatencyMs { get; init; }

    public int ActiveStreams { get; init; }

    /// <summary>
    /// Inference requests currently being forwarded, streaming or not. <see cref="ActiveStreams"/>
    /// is the streaming subset of this, so a non-streaming call in progress moves this and not that.
    /// </summary>
    public int ActiveRequests { get; init; }

    /// <summary>Per-model breakdown of <see cref="ActiveRequests"/>; models at zero are omitted.</summary>
    public IReadOnlyDictionary<string, int> ActiveRequestsPerModel { get; init; } =
        new Dictionary<string, int>();

    public long RateLimitRejections { get; init; }

    public long QuotaRejections { get; init; }

    public IReadOnlyDictionary<string, long> RequestsPerModel { get; init; } =
        new Dictionary<string, long>();

    public IReadOnlyDictionary<string, long> ErrorsPerModel { get; init; } =
        new Dictionary<string, long>();
}
