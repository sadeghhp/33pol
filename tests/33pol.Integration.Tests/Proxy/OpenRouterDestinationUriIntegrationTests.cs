using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

public sealed class OpenRouterDestinationUriIntegrationTests
{
    [Fact]
    public async Task PostChatCompletions_OpenRouterStyleBaseUrl_ForwardsToApiV1Path()
    {
        var handler = new MockUpstreamHandler();
        var modelsJsonPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            modelsJsonPath,
            """
            {
              "models": [
                {
                  "id": "cloud-model",
                  "url": "https://openrouter.example/api",
                  "maxContextLength": 8192,
                  "aliases": ["gpt-cloud"]
                }
              ]
            }
            """,
            Encoding.UTF8);

        try
        {
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
            var response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(
                    """{"model":"gpt-cloud","stream":false}""",
                    Encoding.UTF8,
                    "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            handler.LastRequest.Should().NotBeNull();
            handler.LastRequest!.RequestUri!.AbsoluteUri
                .Should().Be("https://openrouter.example/api/v1/chat/completions");
        }
        finally
        {
            File.Delete(modelsJsonPath);
        }
    }
}
