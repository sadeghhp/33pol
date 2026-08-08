using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Diagnostics;
using Pol33.Core.Models;

namespace Pol33.Api.Services;

public sealed class AdminModelTestService(
    IModelRegistry registry,
    IUpstreamBearerTokenResolver tokenResolver,
    IHttpClientFactory httpClientFactory,
    IGatewayLogStore logStore)
{
    public const string LogCategory = "ModelTest";

    /// <summary>
    /// Dedicated client that refuses redirects.
    /// </summary>
    /// <remarks>
    /// This no longer borrows the inference client, which follows redirects: the probe returns the
    /// upstream's status to the caller, so a 3xx could steer that response oracle at a host the
    /// operator never configured. Private and loopback targets stay allowed — local upstreams are a
    /// primary supported deployment, and the probe only reaches URLs already configured as a model's
    /// upstream.
    /// </remarks>
    public const string HttpClientName = "admin-model-test";

    public const string DefaultPrompt = "ping";

    public const int DefaultMaxTokens = 5;

    public const int MaxMaxTokens = 16;

    public const int MaxPromptLength = 256;

    public const int MaxContentLength = 200;

    /// <summary>
    /// Two short sentences rather than one: an embedding upstream that mishandles batched input
    /// fails on a list where it would pass on a single string, so the probe exercises the real shape.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultEmbeddingInputs =
    [
        "This is a test sentence.",
        "This sentence is used for similarity testing.",
    ];

    /// <summary>
    /// The upstream request a health check sends for one model type. IsHealthyBody decides whether a
    /// 2xx really is a pass — a chat model may legitimately return empty content, but an embeddings
    /// response with no vectors is a failure however the upstream framed it. SummarizeSuccess is
    /// display-only and may return null.
    /// </summary>
    public sealed record ModelTestProbe(
        Uri RequestUri,
        string EndpointPath,
        object Payload,
        Func<string, bool> IsHealthyBody,
        Func<string, string?> SummarizeSuccess);

    public async Task<AdminModelTestResponse> TestAsync(
        string modelId,
        AdminModelTestRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (!registry.TryGetModel(modelId, out var model) || model is null)
        {
            return Fail(new AdminModelTestResponse
            {
                Ok = false,
                ModelId = modelId,
                Detail = $"Model '{modelId}' is not registered.",
                Hint = "The gateway has no model under this id. Reload the models list — it may have " +
                       "been removed or renamed in another session.",
                SuggestedStatusCode = StatusCodes.Status404NotFound,
            }, "model.not_registered");
        }

        var modelType = ModelTypes.Resolve(model);

        var bearer = tokenResolver.ResolveBearerToken(model.UpstreamAuth);
        if (RequiresBearerToken(model.UpstreamAuth) && string.IsNullOrWhiteSpace(bearer))
        {
            return Fail(new AdminModelTestResponse
            {
                Ok = false,
                ModelId = model.Id,
                ModelType = modelType,
                Detail = "Upstream auth is configured but no API key is available. Set a stored key in admin or configure the gateway environment variable.",
                Hint = string.IsNullOrWhiteSpace(model.UpstreamAuth?.EnvVar)
                    ? "Open Routing → Models, edit this model, and set its upstream API key."
                    : $"Set the '{model.UpstreamAuth!.EnvVar}' environment variable on the gateway process, " +
                      "or store the key directly on the model in Routing → Models.",
                SuggestedStatusCode = StatusCodes.Status400BadRequest,
            }, "upstream.missing_credential");
        }

        if (!Uri.TryCreate(model.Url, UriKind.Absolute, out _))
        {
            return Fail(new AdminModelTestResponse
            {
                Ok = false,
                ModelId = model.Id,
                ModelType = modelType,
                Detail = "Model upstream URL is invalid.",
                Hint = $"'{model.Url}' is not an absolute URL. Use the full server root including scheme, " +
                       "e.g. http://127.0.0.1:8000.",
                SuggestedStatusCode = StatusCodes.Status400BadRequest,
            }, "model.invalid_url");
        }

        var probe = BuildProbe(model, modelType, request);
        if (probe is null)
        {
            return new AdminModelTestResponse
            {
                Ok = false,
                ModelId = model.Id,
                ModelType = modelType,
                Supported = false,
                Detail = $"No automated health check is defined for model type '{modelType}'. Verify this model by calling its upstream directly.",
                SuggestedStatusCode = StatusCodes.Status200OK,
            };
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, probe.RequestUri);
        if (!string.IsNullOrWhiteSpace(bearer))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        httpRequest.Content = JsonContent.Create(probe.Payload);

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
                return Fail(
                    new AdminModelTestResponse
                    {
                        Ok = false,
                        ModelId = model.Id,
                        ModelType = modelType,
                        Endpoint = probe.EndpointPath,
                        LatencyMs = stopwatch.ElapsedMilliseconds,
                        StatusCode = statusCode,
                        Detail = TruncateDetail(ExtractErrorDetail(body) ?? $"Upstream returned HTTP {statusCode}."),
                        Hint = GatewayLogHints.ForUpstreamStatus(statusCode, model.Url, probe.EndpointPath, model.Id),
                        SuggestedStatusCode = StatusCodes.Status200OK,
                    },
                    $"upstream.http_{statusCode}",
                    // The raw body is what distinguishes "no such route" from "no such model" on a 404,
                    // and it is routinely dropped by ExtractErrorDetail when the upstream returns HTML.
                    rawBody: body,
                    requestUri: probe.RequestUri);
            }

            if (!probe.IsHealthyBody(body))
            {
                // A 2xx whose body does not carry the payload the probe asked for is not a healthy
                // model — reporting success here is exactly what masked the embedding failures.
                return Fail(
                    new AdminModelTestResponse
                    {
                        Ok = false,
                        ModelId = model.Id,
                        ModelType = modelType,
                        Endpoint = probe.EndpointPath,
                        LatencyMs = stopwatch.ElapsedMilliseconds,
                        StatusCode = statusCode,
                        Detail = TruncateDetail(
                            $"Upstream returned HTTP {statusCode} but the response did not match the expected {modelType} shape."),
                        Hint = $"The call succeeded but the body carried no {modelType} payload. The most " +
                               "likely cause is a model-type mismatch — check the type set in Routing → " +
                               "Models against what this upstream actually serves.",
                        SuggestedStatusCode = StatusCodes.Status200OK,
                    },
                    "upstream.unexpected_shape",
                    rawBody: body,
                    requestUri: probe.RequestUri);
            }

            return new AdminModelTestResponse
            {
                Ok = true,
                ModelId = model.Id,
                ModelType = modelType,
                Endpoint = probe.EndpointPath,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                StatusCode = statusCode,
                Content = TruncateContent(probe.SummarizeSuccess(body)),
                SuggestedStatusCode = StatusCodes.Status200OK,
            };
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Fail(
                new AdminModelTestResponse
                {
                    Ok = false,
                    ModelId = model.Id,
                    ModelType = modelType,
                    Endpoint = probe.EndpointPath,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Detail = "Request timed out while calling upstream.",
                    Hint = GatewayLogHints.ForException(ex),
                    SuggestedStatusCode = StatusCodes.Status200OK,
                },
                "upstream.timeout",
                rawBody: ex.ToString(),
                requestUri: probe.RequestUri);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return Fail(
                new AdminModelTestResponse
                {
                    Ok = false,
                    ModelId = model.Id,
                    ModelType = modelType,
                    Endpoint = probe.EndpointPath,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    Detail = TruncateDetail(ex.Message),
                    Hint = GatewayLogHints.ForException(ex),
                    SuggestedStatusCode = StatusCodes.Status200OK,
                },
                "upstream.unreachable",
                rawBody: ex.ToString(),
                requestUri: probe.RequestUri);
        }
    }

    /// <summary>
    /// Writes a failed probe into the operator log and returns it unchanged, so a test run the
    /// operator dismissed can still be traced afterwards from the Logs tab.
    /// </summary>
    private AdminModelTestResponse Fail(
        AdminModelTestResponse response,
        string eventCode,
        string? rawBody = null,
        Uri? requestUri = null)
    {
        var detail = string.IsNullOrWhiteSpace(rawBody)
            ? null
            : (requestUri is null ? rawBody : $"POST {requestUri}\n\n{rawBody}");

        logStore.Record(new GatewayLogEntry
        {
            Id = $"log_{Guid.NewGuid():N}",
            TimestampUtc = DateTimeOffset.UtcNow,
            Level = GatewayLogLevel.Error.ToString(),
            Category = LogCategory,
            EventCode = eventCode,
            Message = BuildLogMessage(response),
            Detail = detail ?? (requestUri is null ? null : $"POST {requestUri}"),
            Hint = response.Hint,
            ModelId = response.ModelId,
        });

        return response;
    }

    private static string BuildLogMessage(AdminModelTestResponse response)
    {
        var status = response.StatusCode is { } code ? $"HTTP {code}" : "no response";
        var endpoint = response.Endpoint is null ? string.Empty : $" {response.Endpoint}";
        return $"Model test failed for '{response.ModelId}'{endpoint} — {status}. " +
               (response.Detail ?? string.Empty).Trim();
    }

    /// <summary>
    /// Picks the type-specific upstream call. Returns null when the type has no health check the
    /// gateway can express — better to say so than to send a chat payload a video model will reject.
    /// </summary>
    public static ModelTestProbe? BuildProbe(
        ModelConfig model,
        string modelType,
        AdminModelTestRequest? request)
    {
        ArgumentNullException.ThrowIfNull(model);

        switch (modelType)
        {
            case ModelTypes.Embedding:
                return new ModelTestProbe(
                    BuildEmbeddingsUri(model.Url),
                    "/v1/embeddings",
                    new
                    {
                        model = model.Id,
                        input = NormalizeEmbeddingInputs(request?.Prompt),
                    },
                    body => TryExtractEmbeddingSummary(body) is not null,
                    TryExtractEmbeddingSummary);

            case ModelTypes.Rerank:
                return new ModelTestProbe(
                    BuildRerankUri(model.Url),
                    "/v1/rerank",
                    new
                    {
                        model = model.Id,
                        query = NormalizePrompt(request?.Prompt),
                        documents = new[] { "test document" },
                    },
                    body => HasJsonArray(body, "results"),
                    TryExtractRerankScore);

            // OCR models are served over the chat route as vision models, so a chat probe is correct.
            case ModelTypes.TextGeneration:
            case ModelTypes.Ocr:
                return new ModelTestProbe(
                    BuildChatCompletionsUri(model.Url),
                    "/v1/chat/completions",
                    new
                    {
                        model = model.Id,
                        messages = new[] { new { role = "user", content = NormalizePrompt(request?.Prompt) } },
                        max_tokens = ClampMaxTokens(request?.MaxTokens),
                        stream = false,
                    },
                    body => HasJsonArray(body, "choices"),
                    TryExtractAssistantContent);

            default:
                return null;
        }
    }

    public static Uri BuildChatCompletionsUri(string baseUrl) => BuildUpstreamUri(baseUrl, "v1/chat/completions");

    public static Uri BuildRerankUri(string baseUrl) => BuildUpstreamUri(baseUrl, "v1/rerank");

    public static Uri BuildEmbeddingsUri(string baseUrl) => BuildUpstreamUri(baseUrl, "v1/embeddings");

    private static Uri BuildUpstreamUri(string baseUrl, string relativePath)
    {
        var normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(normalizedBase, UriKind.Absolute), relativePath);
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

    /// <summary>
    /// The embeddings probe input. Defaults to the two-sentence batch; an operator-supplied prompt
    /// replaces the first sentence so a custom probe still exercises batching.
    /// </summary>
    public static string[] NormalizeEmbeddingInputs(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt)
            ? [.. DefaultEmbeddingInputs]
            : [NormalizePrompt(prompt), DefaultEmbeddingInputs[1]];

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

    /// <summary>True when <paramref name="body"/> is JSON carrying a non-empty array at <paramref name="propertyName"/>.</summary>
    public static bool HasJsonArray(string body, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty(propertyName, out var array) &&
                   array.ValueKind == JsonValueKind.Array &&
                   array.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
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

    /// <summary>
    /// Summarises an embeddings response as vector count and dimensions. Vectors are far too large
    /// to echo back, and their shape is what a health check actually needs to confirm.
    /// </summary>
    public static string? TryExtractEmbeddingSummary(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array ||
                data.GetArrayLength() == 0)
            {
                return null;
            }

            var vectors = 0;
            var dimensions = -1;

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("embedding", out var embedding) ||
                    embedding.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var length = embedding.GetArrayLength();
                if (length == 0)
                {
                    return null;
                }

                // Ragged vectors mean the upstream is not returning a usable embedding batch.
                if (dimensions >= 0 && length != dimensions)
                {
                    return null;
                }

                dimensions = length;
                vectors++;
            }

            return $"{vectors} embedding{(vectors == 1 ? string.Empty : "s")} × {dimensions} dimensions";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? TryExtractRerankScore(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("relevance_score", out var score) ||
                    score.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                return score.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
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
