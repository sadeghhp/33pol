namespace Pol33.Core.Abstractions;

public interface IGatewayMetricsCollector
{
    void RecordRateLimitRejection(string reason);

    /// <summary>
    /// Rate-limit rejection with the dimensions the Overview's policy card needs. The default body
    /// preserves the old single-argument behaviour for implementations that do not track them.
    /// </summary>
    /// <param name="tenantId">The rejected partition (tenant id, or <c>anon:&lt;ip&gt;</c>); null when unknown.</param>
    /// <param name="modelId">Known only for controls that run after routing (stream concurrency); null otherwise.</param>
    void RecordRateLimitRejection(string reason, string? tenantId, string? modelId) => RecordRateLimitRejection(reason);

    void RecordQuotaRejection();

    /// <inheritdoc cref="RecordRateLimitRejection(string, string?, string?)"/>
    void RecordQuotaRejection(string? tenantId, string? modelId) => RecordQuotaRejection();

    /// <summary>A hard-stop budget refused the request before forwarding.</summary>
    void RecordBudgetRejection(string? tenantId, string? budgetName, string modelId)
    {
    }

    /// <summary>The API key is not granted the model it asked for.</summary>
    void RecordGrantDenial(string? tenantId, string modelId)
    {
    }

    /// <summary>A resolve outcome plus the name the client actually sent, so unknown models can be listed.</summary>
    void RecordModelResolve(string result, string? requestedModel) => RecordModelResolve(result);

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

    /// <summary>
    /// Requests currently waiting in the per-model bulkhead queue for a forwarding slot. A
    /// persistently non-zero value means the model is saturated: the GPU (or its
    /// <c>Gateway:Resilience:MaxConcurrentForwardsPerModel</c> ceiling) is the bottleneck.
    /// </summary>
    void RecordBulkheadQueuedChange(string modelId, int delta)
    {
    }

    void RecordTimeToFirstToken(string modelId, double seconds);

    /// <summary>
    /// Publishes the outcome of a billing reconciliation sweep: how many rollup buckets disagreed
    /// with the ledger behind them, and by how much money in total.
    /// </summary>
    /// <remarks>
    /// Alert on a non-zero <paramref name="discrepancyCount"/>. Everything an operator reads comes
    /// from the rollups while the ledger is what records the request, so a divergence between them
    /// produces wrong numbers that look entirely normal — this counter is the only place it surfaces.
    /// A sweep that runs and finds nothing still reports zero, which is what distinguishes "balanced"
    /// from "the job stopped running".
    /// </remarks>
    void RecordBillingReconciliation(int discrepancyCount, double absoluteCostDrift);

    /// <summary>
    /// Usage events the batch writer gave up on (retries exhausted, or the buffer cap was hit during
    /// an outage). Each one is a request that is NOT in the billing ledger; the Observability
    /// implementation counts it on <c>gateway_usage_writer_dropped_total</c>.
    /// </summary>
    void RecordUsageEventsDropped(int count)
    {
    }
}
