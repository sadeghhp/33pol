using System.Net;
using System.Text;
using System.Text.Json;
using Pol33.Conformance.Tests.Support;

namespace Pol33.Conformance.Tests.Inference;

public sealed class RerankConformanceTests
{
    [Fact]
    public async Task PostRerank_ResponseHasRequiredFields()
    {
        var upstream = new RerankUpstreamHandler();
        await using var factory = ConformanceGatewayFactory.Create(upstream);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/rerank",
            JsonBody("""{"model":"gpt-local","query":"test","documents":["doc1"]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.TryGetProperty("results", out var results).Should().BeTrue();
        results.GetArrayLength().Should().BeGreaterThan(0);

        var first = results[0];
        first.GetProperty("index").GetInt32().Should().Be(0);
        first.GetProperty("relevance_score").GetDouble().Should().BeApproximately(0.95, 0.001);
        first.GetProperty("document").GetProperty("text").GetString().Should().Be("doc1");

        root.GetProperty("usage").GetProperty("total_tokens").GetInt32().Should().Be(56);
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class RerankUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string body =
                """
                {
                  "id": "rerank-abc123",
                  "model": "local-mock",
                  "usage": { "total_tokens": 56 },
                  "results": [
                    {
                      "index": 0,
                      "document": { "text": "doc1" },
                      "relevance_score": 0.95
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
}
