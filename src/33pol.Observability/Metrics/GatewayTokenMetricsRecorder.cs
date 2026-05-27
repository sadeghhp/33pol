namespace Pol33.Observability.Metrics;

internal static class GatewayTokenMetricsRecorder
{
    public static void Record(string modelId, long promptTokens, long completionTokens)
    {
        if (promptTokens > 0)
        {
            GatewayMeters.TokensTotal.Add(
                promptTokens,
                new KeyValuePair<string, object?>("model", modelId),
                new KeyValuePair<string, object?>("direction", "input"));
        }

        if (completionTokens > 0)
        {
            GatewayMeters.TokensTotal.Add(
                completionTokens,
                new KeyValuePair<string, object?>("model", modelId),
                new KeyValuePair<string, object?>("direction", "output"));
        }

        var total = promptTokens + completionTokens;
        if (total > 0)
        {
            GatewayMeters.TokensTotal.Add(
                total,
                new KeyValuePair<string, object?>("model", modelId),
                new KeyValuePair<string, object?>("direction", "total"));
        }
    }
}
