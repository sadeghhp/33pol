namespace Pol33.Core.Abstractions;

public interface IGatewayMetricsCollector
{
    void RecordRateLimitRejection(string reason);

    void RecordQuotaRejection();

    void RecordTokenUsage(string modelId, long promptTokens, long completionTokens);

    void RecordUsageParseFailure(string modelId);

    void RecordInferenceRouted(string modelId, string route, bool isStreaming);

    void RecordForwardAttempt(string modelId, string outcome);

    void RecordModelResolve(string result);

    void RecordCircuitBreakerTransition(string modelId, string toState);

    void RecordBulkheadRejection(string modelId);

    void RecordBulkheadInflightChange(string modelId, int delta);
}
