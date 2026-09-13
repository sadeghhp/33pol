namespace Pol33.Core.Forwarding;

/// <summary>
/// HttpContext.Items keys used by the inference forwarder for observability.
/// </summary>
public static class InferenceForwardingContextKeys
{
    public const string StartedUtc = "GatewayInferenceStartedUtc";

    public const string ModelId = "GatewayInferenceModelId";

    public const string TimeToFirstTokenRecorded = "GatewayInferenceTtftRecorded";

    /// <summary>Milliseconds from forward start to the first response byte, set by the forwarder for streaming responses.</summary>
    public const string TimeToFirstTokenMs = "GatewayInferenceTtftMs";

    /// <summary>
    /// Response-body bytes the forwarder actually delivered to the client, set when the body phase
    /// ends however it ends. Zero on a stall means the upstream answered with headers and nothing
    /// else — a time-to-first-token failure, not a mid-stream one — which is the distinction an
    /// operator needs and the outcome name alone cannot make.
    /// </summary>
    public const string ResponseBytesForwarded = "GatewayInferenceResponseBytesForwarded";
}
