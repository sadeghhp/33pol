namespace Pol33.Core.Models;

public sealed class AdminSummarySnapshot
{
    public required string Uptime { get; init; }

    public long UptimeSeconds { get; init; }

    public long TotalInferenceRequests { get; init; }

    public long TotalErrors { get; init; }

    public double AverageLatencyMs { get; init; }

    public int ActiveStreams { get; init; }

    public long RateLimitRejections { get; init; }

    public long QuotaRejections { get; init; }

    public IReadOnlyDictionary<string, long> RequestsPerModel { get; init; } =
        new Dictionary<string, long>();

    public IReadOnlyDictionary<string, long> ErrorsPerModel { get; init; } =
        new Dictionary<string, long>();
}
