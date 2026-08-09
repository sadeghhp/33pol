using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Summary;

public sealed class GatewayAdminSummaryReader(GatewayRuntimeState runtimeState) : IAdminSummaryReader
{
    public AdminSummarySnapshot GetSnapshot()
    {
        var (total, errors, avgMs, activeStreams, rateLimit, quota) = runtimeState.GetStats();
        var elapsed = DateTimeOffset.UtcNow - runtimeState.StartedUtc;
        var totalSeconds = (long)Math.Max(0, elapsed.TotalSeconds);

        return new AdminSummarySnapshot
        {
            Uptime = FormatUptime(elapsed),
            UptimeSeconds = totalSeconds,
            TotalInferenceRequests = total,
            TotalErrors = errors,
            AverageLatencyMs = avgMs,
            ActiveStreams = activeStreams,
            ActiveRequests = runtimeState.GetActiveRequests(),
            ActiveRequestsPerModel = runtimeState.GetActiveRequestsPerModel(),
            RateLimitRejections = rateLimit,
            QuotaRejections = quota,
            RequestsPerModel = runtimeState.GetRequestsPerModel(),
            ErrorsPerModel = runtimeState.GetErrorsPerModel(),
        };
    }

    private static string FormatUptime(TimeSpan elapsed)
    {
        var days = (int)elapsed.TotalDays;
        var remainder = elapsed - TimeSpan.FromDays(days);
        return $"{days:D2}.{remainder.Hours:D2}:{remainder.Minutes:D2}:{remainder.Seconds:D2}";
    }
}
