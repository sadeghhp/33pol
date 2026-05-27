using System.Net;
using System.Text;
using System.Text.Json;
using Pol33.Conformance.Tests.Support;

namespace Pol33.Conformance.Tests.Inference;

public sealed class OpenAiEmbeddingsConformanceTests
{
    [Fact]
    public async Task PostEmbeddings_ResponseHasRequiredOpenAiFields()
    {
        var upstream = new EmbeddingsUpstreamHandler();
        await using var factory = ConformanceGatewayFactory.Create(upstream);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/embeddings",
            JsonBody("""{"model":"gpt-local","input":"hello"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("object").GetString().Should().Be("list");
        root.TryGetProperty("data", out var data).Should().BeTrue();
        data.GetArrayLength().Should().BeGreaterThan(0);
        data[0].GetProperty("object").GetString().Should().Be("embedding");
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class EmbeddingsUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string body =
                """
                {
                  "object": "list",
                  "data": [
                    {
                      "object": "embedding",
                      "index": 0,
                      "embedding": [0.1, 0.2]
                    }
                  ],
                  "model": "local-mock",
                  "usage": { "prompt_tokens": 2, "total_tokens": 2 }
                }
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
