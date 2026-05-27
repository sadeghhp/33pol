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
    public async Task RemoveModelAsync_WithTwoModels_RemovesTargetAndPersists()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = await WriteTempConfigAsync("""
            { "models": [
              { "id": "keep", "url": "http://keep", "aliases": [] },
              { "id": "remove-me", "url": "http://remove", "aliases": [] }
            ] }
            """);

        try
        {
            await registry.LoadModelsAsync(path);
            var writer = CreateWriter(registry, path);

            var result = await writer.RemoveModelAsync("remove-me");

            result.Success.Should().BeTrue();
            registry.ModelExists("remove-me").Should().BeFalse();
            registry.ModelExists("keep").Should().BeTrue();

            var json = await File.ReadAllTextAsync(path);
            json.Should().Contain("keep");
            json.Should().NotContain("remove-me");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_UnknownId_Returns404()
    {
        var (writer, _, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.UpdateModelAsync("missing", new ModelConfig
            {
                Id = "missing",
                Url = "http://missing",
                Aliases = [],
            });

            result.Success.Should().BeFalse();
            result.SuggestedStatusCode.Should().Be(404);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveModelAsync_UnknownId_Returns404()
    {
        var (writer, _, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.RemoveModelAsync("missing");

            result.Success.Should().BeFalse();
            result.SuggestedStatusCode.Should().Be(404);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddModelAsync_SecretUpstreamEnvVar_ReturnsFailure()
    {
        var (writer, registry, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.AddModelAsync(new ModelConfig
            {
                Id = "bad-auth",
                Url = "https://openrouter.ai/api",
                Aliases = [],
                UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "sk-or-v1-abcdef0123456789" }
            });

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("not the API key");
            registry.ModelExists("bad-auth").Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddModelAsync_MissingUrl_ReturnsFailure()
    {
        var (writer, registry, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.AddModelAsync(new ModelConfig
            {
                Id = "no-url",
                Url = "",
                Aliases = [],
            });

            result.Success.Should().BeFalse();
            registry.ModelExists("no-url").Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddModelAsync_AfterLoad_ModelVisibleImmediately()
    {
        var (writer, registry, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.AddModelAsync(new ModelConfig
            {
                Id = "new-model",
                Url = "http://new:8000",
                Aliases = ["new-alias"],
            });

            result.Success.Should().BeTrue();
            registry.TryGetModel("new-alias", out var model).Should().BeTrue();
            model!.Id.Should().Be("new-model");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddModelAsync_PersistsModelsJsonOnDisk()
    {
        var (writer, _, path) = await CreateWriterWithSeedAsync();

        try
        {
            await writer.AddModelAsync(new ModelConfig
            {
                Id = "disk-model",
                Url = "http://disk:8000",
                Aliases = [],
            });

            var json = await File.ReadAllTextAsync(path);
            using var document = JsonDocument.Parse(json);
            var ids = document.RootElement
                .GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString())
                .ToList();

            ids.Should().Contain("disk-model");
            ids.Should().Contain("seed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddModelAsync_DuplicateId_Returns409()
    {
        var (writer, _, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.AddModelAsync(new ModelConfig
            {
                Id = "seed",
                Url = "http://dup:8000",
                Aliases = [],
            });

            result.Success.Should().BeFalse();
            result.SuggestedStatusCode.Should().Be(409);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RemoveModelAsync_LastModel_Returns400AndKeepsRegistry()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "only", "url": "http://only", "aliases": [] } ] }
            """);

        try
        {
            await registry.LoadModelsAsync(path);
            var writer = CreateWriter(registry, path);

            var result = await writer.RemoveModelAsync("only");

            result.Success.Should().BeFalse();
            result.SuggestedStatusCode.Should().Be(400);
            registry.GetAllModels().Should().HaveCount(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplaceAllAsync_EmptyList_KeepsRegistryUnchanged()
    {
        var (writer, registry, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.ReplaceAllAsync([]);

            result.Success.Should().BeFalse();
            result.SuggestedStatusCode.Should().Be(400);
            registry.GetAllModels().Should().HaveCount(1);
            registry.ModelExists("seed").Should().BeTrue();

            var json = await File.ReadAllTextAsync(path);
            json.Should().Contain("seed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReplaceAllAsync_ValidList_ReplacesRegistryAndFile()
    {
        var (writer, registry, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.ReplaceAllAsync(
            [
                new ModelConfig { Id = "only-new", Url = "http://new-only", Aliases = ["new-alias"] },
            ]);

            result.Success.Should().BeTrue();
            registry.GetAllModels().Should().HaveCount(1);
            registry.TryGetModel("new-alias", out var model).Should().BeTrue();
            model!.Id.Should().Be("only-new");

            var json = await File.ReadAllTextAsync(path);
            json.Should().Contain("only-new");
            json.Should().NotContain("seed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateModelAsync_ExistingId_UpdatesUrlAndPersists()
    {
        var (writer, registry, path) = await CreateWriterWithSeedAsync();

        try
        {
            var result = await writer.UpdateModelAsync("seed", new ModelConfig
            {
                Id = "ignored-id",
                Url = "http://updated:9000",
                Aliases = ["updated-alias"],
            });

            result.Success.Should().BeTrue();
            registry.TryGetModel("updated-alias", out var model).Should().BeTrue();
            model!.Url.Should().Be("http://updated:9000");

            var json = await File.ReadAllTextAsync(path);
            json.Should().Contain("http://updated:9000");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AddModelAsync_ConcurrentReadsDuringApply_DoNotThrow()
    {
        var (writer, registry, path) = await CreateWriterWithSeedAsync();

        try
        {
            var readGate = new ManualResetEventSlim(false);
            var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                readGate.Wait();
                for (var i = 0; i < 200; i++)
                {
                    registry.TryGetModel("seed", out ModelConfig? _);
                    _ = registry.GetAllModels().Count;
                }
            })).ToArray();

            readGate.Set();
            await writer.AddModelAsync(new ModelConfig
            {
                Id = "concurrent",
                Url = "http://concurrent:8000",
                Aliases = [],
            });
            await Task.WhenAll(readers);

            registry.ModelExists("concurrent").Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<(ModelRegistryWriter Writer, ModelRegistryService Registry, string Path)> CreateWriterWithSeedAsync()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var path = await WriteTempConfigAsync("""
            { "models": [ { "id": "seed", "url": "http://seed", "aliases": [] } ] }
            """);
        await registry.LoadModelsAsync(path);
        return (CreateWriter(registry, path), registry, path);
    }

    private static ModelRegistryWriter CreateWriter(ModelRegistryService registry, string path)
    {
        var options = Options.Create(new GatewayOptions { ModelsConfigPath = path });
        return new ModelRegistryWriter(registry, new RegistryGate(), options, NullLogger<ModelRegistryWriter>.Instance);
    }

    private static async Task<string> WriteTempConfigAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"33pol-writer-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }
}
