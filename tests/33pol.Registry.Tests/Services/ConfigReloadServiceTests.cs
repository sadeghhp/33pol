using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ConfigReloadServiceTests
{
    [Fact]
    public async Task ReloadAsync_ConcurrentSecondCall_Returns409()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns(
        [
            new ModelConfig { Id = "a", Url = "http://a" },
        ]);
        registry.LoadModelsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);

        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "a", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            var service = CreateService(registry, path);
            var first = service.ReloadAsync();
            await Task.Delay(50);

            var second = await service.ReloadAsync();

            second.Status.Should().Be("error");
            second.SuggestedStatusCode.Should().Be(409);
            second.Message.Should().Contain("already in progress");

            gate.SetResult();
            await first;
        }
        finally
        {
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
            var service = CreateService(registry, path);
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
            var service = CreateService(registry, path);

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

    [Fact]
    public async Task ScheduleDebouncedReload_FileChange_AppliesAfterDebounce()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "first", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            await registry.LoadModelsAsync(path);
            var service = CreateService(registry, path);
            await service.RefreshFileHashAsync(CancellationToken.None);

            await File.WriteAllTextAsync(path, """
                { "models": [
                  { "id": "first", "url": "http://a", "aliases": [] },
                  { "id": "watched", "url": "http://b", "aliases": [] }
                ] }
                """);

            service.ScheduleDebouncedReload();
            await Task.Delay(600);

            registry.ModelExists("watched").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReloadAsync_ValidConfig_ReturnsSuccessAndUpdatesRegistry()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "first", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            await registry.LoadModelsAsync(path);
            var service = CreateService(registry, path);

            await File.WriteAllTextAsync(path, """
                { "models": [
                  { "id": "first", "url": "http://a", "aliases": [] },
                  { "id": "second", "url": "http://b", "aliases": [] }
                ] }
                """);

            var result = await service.ReloadAsync();

            result.Status.Should().Be("success");
            registry.GetAllModels().Should().HaveCount(2);
            registry.ModelExists("second").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReloadFromDiskAsync_MissingFile_ReturnsError()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns([]);
        var missingPath = Path.Combine(Path.GetTempPath(), $"33pol-missing-{Guid.NewGuid():N}.json");
        var service = CreateService(registry, missingPath);

        var result = await service.ReloadFromDiskAsync();

        result.Status.Should().Be("error");
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task RefreshFileHashAsync_MissingFile_AllowsPollWithoutThrowing()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns([]);
        var missingPath = Path.Combine(Path.GetTempPath(), $"33pol-missing-{Guid.NewGuid():N}.json");
        var service = CreateService(registry, missingPath);

        await service.RefreshFileHashAsync(CancellationToken.None);
        await service.PollForChangesAsync(CancellationToken.None);
    }

    [Fact]
    public void GetStatus_WatchDisabled_ReportsWatchEnabledFalse()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns([]);
        var service = CreateService(registry, "/tmp/models.json", registryWatchEnabled: false);

        service.GetStatus().WatchEnabled.Should().BeFalse();
    }

    [Fact]
    public void GetStatus_WatchEnabled_ReportsWatchEnabledTrue()
    {
        var registry = Substitute.For<IModelRegistry>();
        registry.GetAllModels().Returns([]);
        var service = CreateService(registry, "/tmp/models.json", registryWatchEnabled: true);

        service.GetStatus().WatchEnabled.Should().BeTrue();
    }

    private static ConfigReloadService CreateService(
        IModelRegistry registry,
        string configPath,
        bool registryWatchEnabled = false)
    {
        var options = Options.Create(new GatewayOptions
        {
            ModelsConfigPath = configPath,
            ConfigReloadIntervalSeconds = 2,
            RegistryWatchEnabled = registryWatchEnabled,
        });

        return new ConfigReloadService(
            registry,
            new RegistryGate(),
            options,
            NullLogger<ConfigReloadService>.Instance);
    }

    private static async Task<string> WriteTempConfigAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-config-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        return path;
    }
}
