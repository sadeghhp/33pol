using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Infrastructure;

namespace Pol33.Integration.Tests.LiveRegistry;

[Trait("Category", "V1Parity")]
public sealed class LiveRegistryIntegrationTests
{
    [Fact]
    public async Task FileWatch_ManualEdit_AppliesWithoutRestart()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"33pol-lr11-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, """
            {
              "models": [
                { "id": "first", "url": "http://mock-upstream.local", "aliases": [] }
              ]
            }
            """);

        using var factory = new GatewayWebApplicationFactory(
            registryWatchEnabled: true,
            modelsConfigPath: configPath,
            deleteConfigOnDispose: true);

        using var client = factory.CreateClient();
        await WaitForModelAsync(client, "first", TimeSpan.FromSeconds(5));

        await File.WriteAllTextAsync(configPath, """
            {
              "models": [
                { "id": "first", "url": "http://mock-upstream.local", "aliases": [] },
                { "id": "watched-second", "url": "http://mock-upstream.local", "aliases": [] }
              ]
            }
            """);

        await WaitForModelAsync(client, "watched-second", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WriterAddModel_RoutableWithoutProcessRestart()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();

        var writer = factory.CreateWriter();
        await writer.AddModelAsync(new ModelConfig
        {
            Id = "vllm-added-live",
            Url = "http://mock-upstream.local",
            Aliases = ["vllm-alias"],
        });

        using var content = JsonContent.Create(new { model = "vllm-alias", stream = false });
        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Upstream.LastRequestBody.Should().Contain("vllm-added-live");
    }

    [Fact]
    public async Task ConfigReload_StatusAndReload_ReturnExpectedShapes()
    {
        using var factory = new GatewayWebApplicationFactory();
        using var client = factory.CreateClient();

        var status = await client.GetAsync("/admin/api/config/status");
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync()))
        {
            statusJson.RootElement.GetProperty("hotReloadEnabled").GetBoolean().Should().BeTrue();
            statusJson.RootElement.GetProperty("watchEnabled").GetBoolean().Should().BeFalse();
            statusJson.RootElement.GetProperty("models").GetArrayLength().Should().BeGreaterThan(0);
        }

        var reload = await client.PostAsync("/admin/api/config/reload", content: null);
        reload.StatusCode.Should().Be(HttpStatusCode.OK);
        using var reloadJson = JsonDocument.Parse(await reload.Content.ReadAsStringAsync());
        reloadJson.RootElement.GetProperty("status").GetString().Should().Be("success");
        reloadJson.RootElement.GetProperty("currentModelCount").GetInt32().Should().BeGreaterThan(0);
    }

    private static async Task WaitForModelAsync(HttpClient client, string modelId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var detail = await client.GetAsync($"/v1/models/{modelId}");
            if (detail.StatusCode == HttpStatusCode.OK)
            {
                return;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"Model '{modelId}' was not visible before {timeout}.");
    }
}
