using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Registry;

[Trait("Category", "V1Parity")]
public sealed class LiveRegistryIntegrationTests
{
    [Fact]
    public async Task PollMode_ManualFileEdit_AppliesWithoutRestart()
    {
        var path = await WriteTempModelsFileAsync("""
            { "models": [ { "id": "initial", "url": "http://localhost:8080", "aliases": [] } ] }
            """);

        try
        {
            using var factory = GatewayWebApplicationFactory.Create(
                new MockUpstreamHandler(),
                configureConfiguration: config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Gateway:ModelsConfigPath"] = path,
                        ["Gateway:RegistryWatchEnabled"] = "false",
                        ["Gateway:ConfigReloadIntervalSeconds"] = "1",
                    });
                });

            using var client = factory.CreateClient();
            await WaitForRegistryCountAsync(client, 1);
            await Task.Delay(1500);
            await Task.Delay(1500);

            await File.WriteAllTextAsync(path, """
                { "models": [
                  { "id": "initial", "url": "http://localhost:8080", "aliases": [] },
                  { "id": "polled-add", "url": "http://localhost:8080", "aliases": ["poll-alias"] }
                ] }
                """);

            await WaitForRegistryCountAsync(client, 2, timeoutMs: 5000);

            var detail = await client.GetAsync("/v1/models/poll-alias");
            detail.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("id").GetString().Should().Be("polled-add");
        }
        finally
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

    [Fact]
    public async Task WatchMode_ManualFileEdit_AppliesWithoutRestart()
    {
        var path = await WriteTempModelsFileAsync("""
            { "models": [ { "id": "initial", "url": "http://localhost:8080", "aliases": [] } ] }
            """);

        try
        {
            using var factory = GatewayWebApplicationFactory.Create(
                new MockUpstreamHandler(),
                configureConfiguration: config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Gateway:ModelsConfigPath"] = path,
                        ["Gateway:RegistryWatchEnabled"] = "true",
                        ["Gateway:ConfigReloadIntervalSeconds"] = "300",
                    });
                });

            using var client = factory.CreateClient();
            await WaitForRegistryCountAsync(client, 1);

            var updatedJson = """
                { "models": [
                  { "id": "initial", "url": "http://localhost:8080", "aliases": [] },
                  { "id": "watched-add", "url": "http://localhost:8080", "aliases": ["watch-alias"] }
                ] }
                """;
            var stagingPath = path + ".staging";
            await File.WriteAllTextAsync(stagingPath, updatedJson);
            File.Move(stagingPath, path, overwrite: true);
            await File.AppendAllTextAsync(path, "\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

            await WaitForRegistryCountAsync(client, 2, timeoutMs: 30000);

            var detail = await client.GetAsync("/v1/models/watch-alias");
            detail.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            json.RootElement.GetProperty("id").GetString().Should().Be("watched-add");
        }
        finally
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

    [Fact]
    public async Task RegistryWatchEnabled_ReportsTrueInDevelopmentConfiguration()
    {
        using var factory = GatewayWebApplicationFactory.Create(
            configureConfiguration: config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:RegistryWatchEnabled"] = "true",
                });
            });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/admin/api/config/status");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("watchEnabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task WriterAddModel_InferenceWorksWithoutProcessRestart()
    {
        var path = await WriteTempModelsFileAsync("""
            { "models": [ { "id": "seed", "url": "http://localhost:8080", "aliases": [] } ] }
            """);

        try
        {
            var handler = new MockUpstreamHandler();
            using var factory = GatewayWebApplicationFactory.Create(
                handler,
                configureConfiguration: config =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Gateway:ModelsConfigPath"] = path,
                        ["Gateway:RegistryWatchEnabled"] = "false",
                    });
                });

            using var scope = factory.Services.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<IModelRegistryWriter>();
            var modelId = $"runtime-{Guid.NewGuid():N}";

            var addResult = await writer.AddModelAsync(new Pol33.Core.Models.ModelConfig
            {
                Id = modelId,
                Url = "http://localhost:8080",
                Aliases = [$"{modelId}-alias"],
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
                        ["Gateway:RegistryWatchEnabled"] = "false",
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
        var directory = Path.Combine(Path.GetTempPath(), $"33pol-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "models.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }
}
