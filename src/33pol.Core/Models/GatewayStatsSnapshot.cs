namespace Pol33.Core.Models;

public sealed class GatewayStatsSnapshot
{
    public required string Uptime { get; init; }

    public long UptimeSeconds { get; init; }

    public long TotalRequests { get; init; }

    public IReadOnlyDictionary<string, long> RequestsPerModel { get; init; } =
        new Dictionary<string, long>();

    public double AverageLatencyMs { get; init; }

    public int ActiveConnections { get; init; }

    public IReadOnlyDictionary<string, long> ErrorsPerModel { get; init; } =
        new Dictionary<string, long>();
}
