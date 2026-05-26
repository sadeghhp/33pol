using System.Net;
using System.Text;
using System.Text.Json;

namespace Pol33.Integration.Tests.Infrastructure;

/// <summary>
/// In-process OpenAI-compatible upstream for <see cref="GatewayWebApplicationFactory"/>.
/// </summary>
public sealed class MockOpenAiUpstreamHandler : HttpMessageHandler
{
    private readonly object _lock = new();

    public string? LastRequestBody { get; private set; }

    public string? LastRequestPath { get; private set; }

    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            LastRequestBody = body;
            LastRequestPath = request.RequestUri?.AbsolutePath;
            RequestCount++;
        }

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var stream = IsStreamingRequest(body);

        if (path.Contains("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse(CreateEmbeddingsBody());
        }

        if (path.Contains("/v1/completions", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("/chat/", StringComparison.OrdinalIgnoreCase))
        {
            return stream
                ? StreamingResponse("completion", "data: {\"choices\":[{\"text\":\"ok\"}]}\n\ndata: [DONE]\n\n")
                : JsonResponse("""{"id":"cmpl-mock","object":"text_completion","choices":[{"text":"ok"}]}""");
        }

        if (stream)
        {
            return StreamingResponse(
                "chat.completion.chunk",
                """
                data: {"id":"chunk-1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"}}]}

                data: {"id":"chunk-2","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"!"}}]}

                data: [DONE]


                """);
        }

        return JsonResponse(
            """
            {
              "id": "chatcmpl-mock",
              "object": "chat.completion",
              "choices": [{ "index": 0, "message": { "role": "assistant", "content": "Hello from mock upstream." }, "finish_reason": "stop" }]
            }
            """);
    }

    public void Reset()
    {
        lock (_lock)
        {
            LastRequestBody = null;
            LastRequestPath = null;
            RequestCount = 0;
        }
    }

    private static bool IsStreamingRequest(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("stream", out var stream) &&
                   stream.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return body.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase) ||
                   body.Contains("\"stream\": true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string CreateEmbeddingsBody() =>
        """
        {
          "object": "list",
          "data": [{ "object": "embedding", "index": 0, "embedding": [0.01, 0.02] }],
          "model": "mock-embedding",
          "usage": { "prompt_tokens": 3, "total_tokens": 3 }
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage StreamingResponse(string _, string sse) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
}
