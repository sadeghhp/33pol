using System.Text.Json;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Observability.Usage;

public static class UsageJsonParser
{
    public static bool TryParseUsage(ReadOnlySpan<byte> json, out long promptTokens, out long completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;

        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            if (!doc.RootElement.TryGetProperty("usage", out var usage))
            {
                return false;
            }

            if (usage.TryGetProperty("prompt_tokens", out var prompt))
            {
                promptTokens = prompt.GetInt64();
            }

            if (usage.TryGetProperty("completion_tokens", out var completion))
            {
                completionTokens = completion.GetInt64();
            }

            return promptTokens > 0 || completionTokens > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static UsageEvent? FromInference(
        string requestId,
        string modelId,
        long promptTokens,
        long completionTokens,
        double durationMs,
        TenantContext? tenant = null) =>
        UsageEventFactory.FromInference(
            requestId,
            modelId,
            promptTokens,
            completionTokens,
            durationMs,
            tenant);
}
