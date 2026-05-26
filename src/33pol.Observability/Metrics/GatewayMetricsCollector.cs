using Pol33.Core.Abstractions;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Metrics;

public sealed class GatewayMetricsCollector(GatewayRuntimeState runtimeState) : IGatewayMetricsCollector
{
    public void RecordRateLimitRejection(string reason)
    {
        runtimeState.RecordRateLimitRejection();
        GatewayMeters.RateLimitRejections.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordQuotaRejection()
    {
        runtimeState.RecordQuotaRejection();
        GatewayMeters.QuotaRejections.Add(1);
    }
}
