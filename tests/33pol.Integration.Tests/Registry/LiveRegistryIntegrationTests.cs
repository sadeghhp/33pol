using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Registry;

[Trait("Category", "V1Parity")]
public sealed class LiveRegistryIntegrationTests
{
    [Fact]
    public async Task WriterAddModel_InferenceWorksWithoutProcessRestart()
    {
        var path = await WriteTempModelsFileAsync("""
            { "models": [ { "id": "seed", "url": "http://localhost:8080", "aliases": [] } ] }
            """);

        try
        {
            var handler = new MockUpstreamHandler();
            using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
                upstreamHandler: handler,
                configureSettings: settings => settings["Gateway:ModelsConfigPath"] = path);

            using var scope = factory.Services.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IModelRegistryWriter>();
            var modelId = $"runtime-{Guid.NewGuid():N}";

            // PublicAccess so the anonymous inference call below is allowed even with a seeded admin key.
            var addResult = await writer.AddModelAsync(new ModelConfig
            {
                Id = modelId,
                Url = "http://localhost:8080",
                Aliases = [$"{modelId}-alias"],
                PublicAccess = true,
            });

            addResult.Success.Should().BeTrue(addResult.Message);

            using var client = factory.CreateClient();
            var response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent(
                    $$"""{"model":"{{modelId}}-alias","stream":false}""",
                    Encoding.UTF8,
                    "application/json"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            handler.LastRequestBody.Should().Contain($"\"model\":\"{modelId}\"");
        }
        finally
        {
            CleanupModelsFile(path);
        }
    }

    [Fact]
    public async Task PostConfigReload_InvalidJson_ReturnsErrorAndKeepsModels()
    {
        var path = await WriteTempModelsFileAsync("""
            { "models": [ { "id": "keep-me", "url": "http://localhost:8080", "aliases": [] } ] }
            """);

        try
        {
            using var factory = GatewayWebApplicationFactory.Create(
                configureConfiguration: config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Gateway:ModelsConfigPath"] = path,
                    });
                });

            using var client = factory.CreateClient();
            await WaitForRegistryCountAsync(client, 1);

            await File.WriteAllTextAsync(path, "{ not-json");
            var reload = await client.PostAsync("/admin/api/config/reload", content: null);

            reload.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            using var json = JsonDocument.Parse(await reload.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("status").GetString().Should().Be("error");

            var models = await client.GetAsync("/v1/models");
            using var list = JsonDocument.Parse(await models.Content.ReadAsStringAsync());
            list.RootElement.GetProperty("data").GetArrayLength().Should().Be(1);
        }
        finally
        {
            CleanupModelsFile(path);
        }
    }

    private static async Task WaitForRegistryCountAsync(
        HttpClient client,
        int expectedCount,
        int timeoutMs = 2000)
    {
        var attempts = Math.Max(1, timeoutMs / 100);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var status = await client.GetAsync("/admin/api/config/status", requestTimeout.Token);
            status.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            if (json.RootElement.GetProperty("modelCount").GetInt32() >= expectedCount)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Registry did not reach {expectedCount} model(s) in time.");
    }

    private static async Task<string> WriteTempModelsFileAsync(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"33pol-registry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "models.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private static void CleanupModelsFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
