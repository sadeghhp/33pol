using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class GatewayStatsService(GatewayProcessClock processClock)
{
    public GatewayStatsSnapshot GetSnapshot()
    {
        var elapsed = DateTimeOffset.UtcNow - processClock.StartedUtc;
        var totalSeconds = (long)Math.Max(0, elapsed.TotalSeconds);

        return new GatewayStatsSnapshot
        {
            Uptime = FormatUptime(elapsed),
            UptimeSeconds = totalSeconds,
            TotalRequests = 0,
            RequestsPerModel = new Dictionary<string, long>(),
            AverageLatencyMs = 0,
            ActiveConnections = 0,
            ErrorsPerModel = new Dictionary<string, long>(),
        };
    }

    private static string FormatUptime(TimeSpan elapsed)
    {
        var days = (int)elapsed.TotalDays;
        var remainder = elapsed - TimeSpan.FromDays(days);
        return $"{days:D2}.{remainder.Hours:D2}:{remainder.Minutes:D2}:{remainder.Seconds:D2}";
    }
}
