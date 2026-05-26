using System.Net;
using System.Text;
using System.Text.Json;
using Pol33.Conformance.Tests.Support;

namespace Pol33.Conformance.Tests.Inference;

/// <summary>
/// GA conformance: non-streaming chat completion preserves OpenAI-compatible top-level fields.
/// </summary>
public sealed class OpenAiChatCompletionConformanceTests
{
    [Fact]
    public async Task PostChatCompletions_NonStream_ResponseHasRequiredOpenAiFields()
    {
        var upstream = new PassthroughUpstreamHandler();
        await using var factory = ConformanceGatewayFactory.Create(upstream);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"gpt-local","stream":false}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("object").GetString().Should().Be("chat.completion");
        root.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("model").GetString().Should().Be("local-mock");
        root.TryGetProperty("choices", out _).Should().BeTrue();
    }

    [Fact]
    public async Task PostChatCompletions_Stream_ResponseIsEventStream()
    {
        await using var factory = ConformanceGatewayFactory.Create(new StreamingUpstreamHandler());
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"gpt-local","stream":true}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Contain("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("data:");
        body.Should().Contain("[DONE]");
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class PassthroughUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string body =
                """
                {
                  "id": "chatcmpl-conformance",
                  "object": "chat.completion",
                  "model": "local-mock",
                  "choices": [
                    {
                      "index": 0,
                      "message": { "role": "assistant", "content": "ok" },
                      "finish_reason": "stop"
                    }
                  ]
                }
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StreamingUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"id\":\"chunk-1\"}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            });
    }
}
