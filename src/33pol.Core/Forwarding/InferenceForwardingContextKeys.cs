namespace Pol33.Core.Forwarding;

/// <summary>
/// HttpContext.Items keys used by the inference forwarder for observability.
/// </summary>
public static class InferenceForwardingContextKeys
{
    public const string StartedUtc = "GatewayInferenceStartedUtc";

    public const string ModelId = "GatewayInferenceModelId";

    public const string TimeToFirstTokenRecorded = "GatewayInferenceTtftRecorded";
}
