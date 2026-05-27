using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

[Trait("Category", "V1Parity")]
public sealed class OpenRouterUpstreamAuthIntegrationTests
{
    [Fact]
    public async Task PostChatCompletions_WithUpstreamAuth_InsertsUpstreamBearerAndStripsClientAuth()
    {
        var handler = new MockUpstreamHandler();

        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", "or_test_token");

        var modelsJsonPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            modelsJsonPath,
            """
            {
              "models": [
                {
                  "id": "local-mock",
                  "url": "http://localhost:8080",
                  "maxContextLength": 8192,
                  "aliases": ["gpt-local"],
                  "upstreamAuth": { "type": "bearer", "envVar": "OPENROUTER_API_KEY" }
                }
              ]
            }
            """,
            Encoding.UTF8);

        using var factory = GatewayWebApplicationFactory.Create(
            handler,
            configureConfiguration: config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:ModelsConfigPath"] = modelsJsonPath,
                });
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-gateway-key-should-not-forward");

        using var body = new StringContent(
            """{"model":"gpt-local","stream":false}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.LastRequest.Should().NotBeNull();

        handler.LastRequest!.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("or_test_token");

        handler.LastRequest.Headers.Contains("X-API-Key").Should().BeFalse();
    }
}

