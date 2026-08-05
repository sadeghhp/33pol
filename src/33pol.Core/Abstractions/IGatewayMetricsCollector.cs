namespace Pol33.Core.Abstractions;

public interface IGatewayMetricsCollector
{
    void RecordRateLimitRejection(string reason);

    void RecordQuotaRejection();

    void RecordTokenUsage(string modelId, long promptTokens, long completionTokens);

    void RecordUsageParseFailure(string modelId);

    /// <summary>
    /// The upstream reported only a combined token total for this model, so its cost was priced by
    /// the conservative total-only policy rather than from a real input/output split. Persistently
    /// non-zero for a model means its billing is an approximation.
    /// </summary>
    void RecordUnsplitUsage(string modelId);

    /// <summary>
    /// A response was billed from an estimate because the authoritative usage never arrived — almost
    /// always a client disconnecting mid-stream. Use it to reconcile approximated spend.
    /// </summary>
    void RecordEstimatedUsage(string modelId);

    void RecordInferenceRouted(string modelId, string route, bool isStreaming);

    void RecordForwardAttempt(string modelId, string outcome);

    void RecordModelResolve(string result);

    void RecordCircuitBreakerTransition(string modelId, string toState);

    void RecordBulkheadRejection(string modelId);

    void RecordBulkheadInflightChange(string modelId, int delta);

    void RecordTimeToFirstToken(string modelId, double seconds);
}
