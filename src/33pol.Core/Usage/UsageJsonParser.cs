using System.Text;
using System.Text.Json;

namespace Pol33.Core.Usage;

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

            if (promptTokens == 0 && completionTokens == 0 &&
                usage.TryGetProperty("total_tokens", out var total))
            {
                promptTokens = total.GetInt64();
            }

            return promptTokens > 0 || completionTokens > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parses OpenAI-style SSE bodies; uses the last <c>data:</c> line that contains a <c>usage</c> object.
    /// </summary>
    public static bool TryParseUsageFromSseText(string sseText, out long promptTokens, out long completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;

        if (string.IsNullOrWhiteSpace(sseText))
        {
            return false;
        }

        var lines = sseText.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line.AsSpan(5).Trim().ToString();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            if (TryParseUsage(Encoding.UTF8.GetBytes(payload), out promptTokens, out completionTokens))
            {
                return true;
            }
        }

        return false;
    }
}
