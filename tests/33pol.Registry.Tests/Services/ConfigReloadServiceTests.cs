using System.Text;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ConfigReloadServiceTests
{
    [Fact]
    public async Task ReloadAsync_ConcurrentSecondCall_Returns409()
    {
        var gate = new RegistryGate();
        await gate.EnterAsync();

        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "a", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            await registry.LoadModelsAsync(path);
            var service = CreateService(registry, path, gate, watchEnabled: false);

            var second = await service.ReloadAsync();

            second.Status.Should().Be("error");
            second.SuggestedStatusCode.Should().Be(409);
            second.Message.Should().Contain("already in progress");
        }
        finally
        {
            gate.Release();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PollForChangesAsync_HashChange_TriggersReload()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "first", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            await registry.LoadModelsAsync(path);
            var service = CreateService(registry, path, new RegistryGate(), watchEnabled: false);
            await service.RefreshFileHashAsync(CancellationToken.None);

            await File.WriteAllTextAsync(path, """
                { "models": [
                  { "id": "first", "url": "http://a", "aliases": [] },
                  { "id": "second", "url": "http://b", "aliases": [] }
                ] }
                """);

            await service.PollForChangesAsync(CancellationToken.None);

            registry.GetAllModels().Should().HaveCount(2);
            registry.ModelExists("second").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReloadAsync_InvalidJson_KeepsPreviousRegistry()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "keep-me", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            await registry.LoadModelsAsync(path);
            var service = CreateService(registry, path, new RegistryGate(), watchEnabled: false);

            await File.WriteAllTextAsync(path, "{ not-json");
            var result = await service.ReloadAsync();

            result.Status.Should().Be("error");
            registry.GetAllModels().Should().HaveCount(1);
            registry.ModelExists("keep-me").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetStatus_IncludesWatchEnabledFlag()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = Path.Combine(Path.GetTempPath(), $"33pol-status-{Guid.NewGuid():N}.json");

        var watchService = CreateService(registry, path, new RegistryGate(), watchEnabled: true);
        var pollService = CreateService(registry, path, new RegistryGate(), watchEnabled: false);

        watchService.GetStatus().WatchEnabled.Should().BeTrue();
        pollService.GetStatus().WatchEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ComputeFileHashAsync_SameContent_ProducesStableHash()
    {
        var path = await WriteTempConfigAsync("""{ "models": [] }""");

        try
        {
            var first = await ConfigReloadService.ComputeFileHashAsync(path, CancellationToken.None);
            var second = await ConfigReloadService.ComputeFileHashAsync(path, CancellationToken.None);

            first.Should().Equal(second);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ConfigReloadService CreateService(
        ModelRegistryService registry,
        string configPath,
        RegistryGate gate,
        bool watchEnabled)
    {
        var options = Options.Create(new GatewayOptions
        {
            ModelsConfigPath = configPath,
            ConfigReloadIntervalSeconds = 2,
            RegistryWatchEnabled = watchEnabled,
        });

        var environment = new HostEnvironmentStub(watchEnabled);

        return new ConfigReloadService(
            registry,
            options,
            gate,
            environment,
            NullLogger<ConfigReloadService>.Instance);
    }

    private static async Task<string> WriteTempConfigAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        return path;
    }

    private sealed class HostEnvironmentStub(bool isDevelopment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = isDevelopment ? Environments.Development : Environments.Production;
        public string ApplicationName { get; set; } = "33pol.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public bool IsDevelopment() => isDevelopment;
    }
}
