using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ModelRegistryWriterTests
{
    [Fact]
    public async Task AddModelAsync_PersistsAndAppliesImmediately()
    {
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "existing", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(path);

            var writer = CreateWriter(registry, path);
            await writer.AddModelAsync(new ModelConfig
            {
                Id = "new-model",
                Url = "http://new",
                Aliases = ["new-alias"],
            });

            registry.ModelExists("new-alias").Should().BeTrue();
            registry.GetAllModels().Should().HaveCount(2);

            var onDisk = JsonSerializer.Deserialize<ModelRegistryConfig>(await File.ReadAllTextAsync(path))!;
            onDisk.Models!.Select(m => m.Id).Should().BeEquivalentTo(["existing", "new-model"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplaceAllAsync_EmptyList_KeepsRegistryAndFileUnchanged()
    {
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "keep", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(path);
            var before = await File.ReadAllTextAsync(path);

            var writer = CreateWriter(registry, path);
            await writer.ReplaceAllAsync([]);

            registry.GetAllModels().Should().HaveCount(1);
            registry.ModelExists("keep").Should().BeTrue();
            (await File.ReadAllTextAsync(path)).Should().Be(before);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddModelAsync_DuplicateId_Throws()
    {
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "dup", "url": "http://a", "aliases": [] } ] }
            """);

        try
        {
            var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(path);
            var writer = CreateWriter(registry, path);

            var act = () => writer.AddModelAsync(new ModelConfig { Id = "dup", Url = "http://b" });

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*already exists*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddModelAsync_ConcurrentWithReads_DoesNotThrow()
    {
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "base", "url": "http://a", "aliases": ["base-alias"] } ] }
            """);

        try
        {
            var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
            await registry.LoadModelsAsync(path);
            var writer = CreateWriter(registry, path);

            var readGate = new ManualResetEventSlim(false);
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                readGate.Wait();
                for (var i = 0; i < 200; i++)
                {
                    registry.TryGetModel("base-alias", out ModelConfig? _);
                    _ = registry.GetAllModels().Count;
                }
            })).ToArray();

            readGate.Set();
            await writer.AddModelAsync(new ModelConfig { Id = "added", Url = "http://added" });
            await Task.WhenAll(readers);

            registry.ModelExists("added").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ModelRegistryWriter CreateWriter(ModelRegistryService registry, string configPath)
    {
        var options = Options.Create(new GatewayOptions { ModelsConfigPath = configPath });
        return new ModelRegistryWriter(registry, options, new RegistryGate(), NullLogger<ModelRegistryWriter>.Instance);
    }

    private static async Task<string> WriteTempConfigAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-writer-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }
}
