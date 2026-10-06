using System.Diagnostics;
using Pol33.Core.Observability;

namespace Pol33.Observability.Metrics;

internal static class GatewayTokenMetricsRecorder
{
    /// <param name="caller">Already resolved through <see cref="MetricCallerBudget"/>.</param>
    public static void Record(string modelId, long promptTokens, long completionTokens, MetricCaller caller)
    {
        if (promptTokens > 0)
        {
            GatewayMeters.TokensTotal.Add(promptTokens, Tags(modelId, "input", caller));
        }

        if (completionTokens > 0)
        {
            GatewayMeters.TokensTotal.Add(completionTokens, Tags(modelId, "output", caller));
        }

        var total = promptTokens + completionTokens;
        if (total > 0)
        {
            GatewayMeters.TokensTotal.Add(total, Tags(modelId, "total", caller));
        }
    }

    private static TagList Tags(string modelId, string direction, MetricCaller caller) =>
        new()
        {
            { "model", modelId },
            { "direction", direction },
            { "tenant", caller.Tenant },
            { "key", caller.Key },
        };
}
