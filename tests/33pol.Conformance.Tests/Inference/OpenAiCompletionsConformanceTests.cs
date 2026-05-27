using System.Net;
using System.Text;
using System.Text.Json;
using Pol33.Conformance.Tests.Support;

namespace Pol33.Conformance.Tests.Inference;

public sealed class OpenAiCompletionsConformanceTests
{
    [Fact]
    public async Task PostCompletions_ResponseHasRequiredOpenAiFields()
    {
        var upstream = new LegacyCompletionUpstreamHandler();
        await using var factory = ConformanceGatewayFactory.Create(upstream);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/completions",
            JsonBody("""{"model":"gpt-local","prompt":"hello"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("object").GetString().Should().Be("text_completion");
        root.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        root.TryGetProperty("choices", out _).Should().BeTrue();
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class LegacyCompletionUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string body =
                """
                {
                  "id": "cmpl-conformance",
                  "object": "text_completion",
                  "model": "local-mock",
                  "choices": [{ "text": "ok", "index": 0, "finish_reason": "stop" }]
                }
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
