namespace Pol33.Core.Abstractions;

public interface IGatewayMetricsCollector
{
    void RecordRateLimitRejection(string reason);

    void RecordQuotaRejection();
}
