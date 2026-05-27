using System.Net.Http.Headers;
using System.Text.Json;

namespace Pol33.Api.Services;

public sealed class OpenAiCompatibleProviderModelsClient(HttpClient http)
{
    public async Task<IReadOnlyList<ProviderModelListItem>> ListModelsAsync(
        Uri modelsListUrl,
        string bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsListUrl);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ProviderModelListItem>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var name = item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString()
                : null;

            int? contextLength = null;
            if (item.TryGetProperty("context_length", out var ctxProp) && ctxProp.ValueKind == JsonValueKind.Number &&
                ctxProp.TryGetInt32(out var ctx))
            {
                contextLength = ctx;
            }

            results.Add(new ProviderModelListItem(id!, name, contextLength));
        }

        return results;
    }
}

public sealed record ProviderModelListItem(
    string Id,
    string? Name,
    int? ContextLength);
