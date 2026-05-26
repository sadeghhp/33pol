using System.Text.Json;

namespace Pol33.Proxy.Parsing;

public readonly record struct InferenceRequestInfo(string? Model, bool Stream);

public static class InferenceRequestParser
{
    public static async Task<InferenceRequestInfo> ParseAsync(
        Stream body,
        CancellationToken cancellationToken = default)
    {
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        string? model = null;
        var stream = false;

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

        return new InferenceRequestInfo(model, stream);
    }
}
