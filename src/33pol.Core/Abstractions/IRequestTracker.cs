namespace Pol33.Core.Abstractions;

public interface IRequestTracker
{
    /// <summary>
    /// Called when an inference request is about to be forwarded (Phase 4 metrics hook).
    /// </summary>
    IInferenceRequestScope BeginInferenceRequest(string modelId, bool isStreaming);

    /// <summary>
    /// Records a request the gateway answered with an error before it ever reached the upstream —
    /// an unhealthy backend, an open circuit, a full bulkhead or an exhausted stream slot.
    /// </summary>
    /// <remarks>
    /// These never open an <see cref="IInferenceRequestScope"/>, so before this existed they reached
    /// the client as a 429/502/503 but left no trace on the dashboard: during a saturation incident
    /// the console showed a calm, error-free gateway. Latency is deliberately not contributed —
    /// admission takes microseconds and would drag the mean toward zero.
    /// </remarks>
    void RecordRejectedRequest(string modelId, string errorCode);
}
