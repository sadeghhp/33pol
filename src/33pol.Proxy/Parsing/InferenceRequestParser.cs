using System.Text.Json;

namespace Pol33.Proxy.Parsing;

public readonly record struct InferenceRequestInfo(string? Model, bool Stream, int? MaxTokens = null);

public static class InferenceRequestParser
{
    public static async Task<InferenceRequestInfo> ParseAsync(
        Stream body,
        CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Request body must be a JSON object.");
        }

        string? model = null;
        var stream = false;
        int? maxTokens = null;

        if (document.RootElement.TryGetProperty("model", out var modelElement) &&
            modelElement.ValueKind == JsonValueKind.String)
        {
            model = modelElement.GetString();
        }

        if (document.RootElement.TryGetProperty("stream", out var streamElement) &&
            streamElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            stream = streamElement.GetBoolean();
        }

        // OpenAI "max_tokens" (legacy) or "max_completion_tokens" (newer); used to estimate the
        // reserved cost for hard-stop budget enforcement.
        if ((document.RootElement.TryGetProperty("max_tokens", out var maxTokensElement) &&
                maxTokensElement.ValueKind == JsonValueKind.Number &&
                maxTokensElement.TryGetInt32(out var parsedMaxTokens)) ||
            (document.RootElement.TryGetProperty("max_completion_tokens", out maxTokensElement) &&
                maxTokensElement.ValueKind == JsonValueKind.Number &&
                maxTokensElement.TryGetInt32(out parsedMaxTokens)))
        {
            if (parsedMaxTokens > 0)
            {
                maxTokens = parsedMaxTokens;
            }
        }

        return new InferenceRequestInfo(model, stream, maxTokens);
    }
}
