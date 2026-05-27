using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class AdminModelTestService(
    IModelRegistry registry,
    IUpstreamBearerTokenResolver tokenResolver,
    IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = Core.Http.UpstreamHttpClientNames.Inference;

    public const string DefaultPrompt = "ping";

    public const int DefaultMaxTokens = 5;

    public const int MaxMaxTokens = 16;

    public const int MaxPromptLength = 256;

    public const int MaxContentLength = 200;

    public async Task<AdminModelTestResponse> TestAsync(
        string modelId,
        AdminModelTestRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!registry.TryGetModel(modelId, out var model) || model is null)
        {
            return new AdminModelTestResponse
            {
                Ok = false,
                ModelId = modelId,
                Detail = $"Model '{modelId}' is not registered.",
                SuggestedStatusCode = StatusCodes.Status404NotFound,
            };
        }

        var bearer = tokenResolver.ResolveBearerToken(model.UpstreamAuth);
        if (RequiresBearerToken(model.UpstreamAuth) && string.IsNullOrWhiteSpace(bearer))
        {
            return new AdminModelTestResponse
            {
                Ok = false,
                ModelId = model.Id,
                Detail = "Upstream auth is configured but no API key is available. Set a stored key in admin or configure the gateway environment variable.",
                SuggestedStatusCode = StatusCodes.Status400BadRequest,
            };
        }

        if (!Uri.TryCreate(model.Url, UriKind.Absolute, out _))
        {
            return new AdminModelTestResponse
            {
                Ok = false,
                ModelId = model.Id,
                Detail = "Model upstream URL is invalid.",
                SuggestedStatusCode = StatusCodes.Status400BadRequest,
            };
        }

        var prompt = NormalizePrompt(request?.Prompt);
        var maxTokens = ClampMaxTokens(request?.MaxTokens);
        var chatUri = BuildChatCompletionsUri(model.Url);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, chatUri);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        httpRequest.Content = JsonContent.Create(new
        {
            model = model.Id,
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = maxTokens,
            stream = false,
        });

        var client = httpClientFactory.CreateClient(HttpClientName);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new AdminModelTestResponse
                {
                    Ok = false,
                    ModelId = model.Id,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    StatusCode = statusCode,
                    Detail = TruncateDetail(ExtractErrorDetail(body) ?? $"Upstream returned HTTP {statusCode}."),
                    SuggestedStatusCode = StatusCodes.Status200OK,
                };
            }

            return new AdminModelTestResponse
            {
                Ok = true,
                ModelId = model.Id,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                StatusCode = statusCode,
                Content = TruncateContent(TryExtractAssistantContent(body)),
                SuggestedStatusCode = StatusCodes.Status200OK,
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new AdminModelTestResponse
            {
                Ok = false,
                ModelId = model.Id,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Detail = "Request timed out while calling upstream.",
                SuggestedStatusCode = StatusCodes.Status200OK,
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new AdminModelTestResponse
            {
                Ok = false,
                ModelId = model.Id,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                Detail = TruncateDetail(ex.Message),
                SuggestedStatusCode = StatusCodes.Status200OK,
            };
        }
    }

    public static Uri BuildChatCompletionsUri(string baseUrl)
    {
        var normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(normalizedBase, UriKind.Absolute), "v1/chat/completions");
    }

    public static int ClampMaxTokens(int? maxTokens)
    {
        var value = maxTokens ?? DefaultMaxTokens;
        if (value < 1)
        {
            return 1;
        }

        return value > MaxMaxTokens ? MaxMaxTokens : value;
    }

    public static string NormalizePrompt(string? prompt)
    {
        var value = string.IsNullOrWhiteSpace(prompt) ? DefaultPrompt : prompt.Trim();
        return value.Length <= MaxPromptLength ? value : value[..MaxPromptLength];
    }

    public static bool RequiresBearerToken(UpstreamAuthConfig? upstreamAuth)
    {
        if (upstreamAuth is null)
        {
            return false;
        }

        if (!string.Equals(upstreamAuth.Type, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(upstreamAuth.SecretRef) ||
               !string.IsNullOrWhiteSpace(upstreamAuth.EnvVar);
    }

    public static string? TryExtractAssistantContent(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var message) ||
                    message.ValueKind != JsonValueKind.Object ||
                    !message.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = content.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string? ExtractErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }

            if (root.TryGetProperty("message", out var topMessage) && topMessage.ValueKind == JsonValueKind.String)
            {
                return topMessage.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string? TruncateContent(string? content) =>
        string.IsNullOrEmpty(content) ? content : Truncate(content, MaxContentLength);

    public static string TruncateDetail(string detail) => Truncate(detail, MaxContentLength);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
