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
}
