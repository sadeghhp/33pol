using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ModelRegistryWriterTests
{
    [Fact]
    public async Task RemoveModelAsync_WithTwoModels_RemovesTargetAndPersists()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        registry.Apply(
        [
            new ModelConfig { Id = "keep", Url = "http://keep", Aliases = [] },
            new ModelConfig { Id = "remove-me", Url = "http://remove", Aliases = [] },
        ]);
        var (writer, repo) = CreateWriter(registry);

        var result = await writer.RemoveModelAsync("remove-me");

        result.Success.Should().BeTrue();
        registry.ModelExists("remove-me").Should().BeFalse();
        registry.ModelExists("keep").Should().BeTrue();
        repo.Saved.Select(m => m.Id).Should().BeEquivalentTo(["keep"]);
    }

    [Fact]
    public async Task UpdateModelAsync_UnknownId_Returns404()
    {
        var (writer, _, _) = CreateWriterWithSeed();

        var result = await writer.UpdateModelAsync("missing", new ModelConfig
        {
            Id = "missing",
            Url = "http://missing",
            Aliases = [],
        });

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(404);
    }

    [Fact]
    public async Task RemoveModelAsync_UnknownId_Returns404()
    {
        var (writer, _, _) = CreateWriterWithSeed();

        var result = await writer.RemoveModelAsync("missing");

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(404);
    }

    [Fact]
    public async Task AddModelAsync_SecretUpstreamEnvVar_ReturnsFailure()
    {
        var (writer, registry, _) = CreateWriterWithSeed();

        var result = await writer.AddModelAsync(new ModelConfig
        {
            Id = "bad-auth",
            Url = "https://openrouter.ai/api",
            Aliases = [],
            UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "sk-or-v1-abcdef0123456789" },
        });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not the API key");
        registry.ModelExists("bad-auth").Should().BeFalse();
    }

    [Fact]
    public async Task AddModelAsync_MissingUrl_ReturnsFailure()
    {
        var (writer, registry, _) = CreateWriterWithSeed();

        var result = await writer.AddModelAsync(new ModelConfig
        {
            Id = "no-url",
            Url = "",
            Aliases = [],
        });

        result.Success.Should().BeFalse();
        registry.ModelExists("no-url").Should().BeFalse();
    }

    [Fact]
    public async Task AddModelAsync_AfterLoad_ModelVisibleImmediately()
    {
        var (writer, registry, _) = CreateWriterWithSeed();

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

    [Fact]
    public async Task AddModelAsync_PersistsToRepository()
    {
        var (writer, _, repo) = CreateWriterWithSeed();

        await writer.AddModelAsync(new ModelConfig
        {
            Id = "db-model",
            Url = "http://db:8000",
            Aliases = [],
        });

        repo.Saved.Select(m => m.Id).Should().Contain("db-model");
        repo.Saved.Select(m => m.Id).Should().Contain("seed");
    }

    [Fact]
    public async Task AddModelAsync_DuplicateId_Returns409()
    {
        var (writer, _, _) = CreateWriterWithSeed();

        var result = await writer.AddModelAsync(new ModelConfig
        {
            Id = "seed",
            Url = "http://dup:8000",
            Aliases = [],
        });

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(409);
    }

    [Fact]
    public async Task RemoveModelAsync_LastModel_Returns400AndKeepsRegistry()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        registry.Apply([new ModelConfig { Id = "only", Url = "http://only", Aliases = [] }]);
        var (writer, _) = CreateWriter(registry);

        var result = await writer.RemoveModelAsync("only");

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(400);
        registry.GetAllModels().Should().HaveCount(1);
    }

    [Fact]
    public async Task ReplaceAllAsync_EmptyList_KeepsRegistryUnchanged()
    {
        var (writer, registry, _) = CreateWriterWithSeed();

        var result = await writer.ReplaceAllAsync([]);

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(400);
        registry.GetAllModels().Should().HaveCount(1);
        registry.ModelExists("seed").Should().BeTrue();
    }

    [Fact]
    public async Task ReplaceAllAsync_ValidList_ReplacesRegistryAndPersists()
    {
        var (writer, registry, repo) = CreateWriterWithSeed();

        var result = await writer.ReplaceAllAsync(
        [
            new ModelConfig { Id = "only-new", Url = "http://new-only", Aliases = ["new-alias"] },
        ]);

        result.Success.Should().BeTrue();
        registry.GetAllModels().Should().HaveCount(1);
        registry.TryGetModel("new-alias", out var model).Should().BeTrue();
        model!.Id.Should().Be("only-new");
        repo.Saved.Select(m => m.Id).Should().BeEquivalentTo(["only-new"]);
    }

    [Fact]
    public async Task UpdateModelAsync_ExistingId_UpdatesUrlAndPersists()
    {
        var (writer, registry, repo) = CreateWriterWithSeed();

        var result = await writer.UpdateModelAsync("seed", new ModelConfig
        {
            Id = "ignored-id",
            Url = "http://updated:9000",
            Aliases = ["updated-alias"],
        });

        result.Success.Should().BeTrue();
        registry.TryGetModel("updated-alias", out var model).Should().BeTrue();
        model!.Url.Should().Be("http://updated:9000");
        repo.Saved.Should().ContainSingle(m => m.Id == "seed" && m.Url == "http://updated:9000");
    }

    [Fact]
    public async Task AddModelAsync_ConcurrentReadsDuringApply_DoNotThrow()
    {
        var (writer, registry, _) = CreateWriterWithSeed();

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

    private static (ModelRegistryWriter Writer, ModelRegistryService Registry, FakeModelRouteRepository Repo) CreateWriterWithSeed()
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        registry.Apply([new ModelConfig { Id = "seed", Url = "http://seed", Aliases = [] }]);
        var (writer, repo) = CreateWriter(registry);
        return (writer, registry, repo);
    }

    private static (ModelRegistryWriter Writer, FakeModelRouteRepository Repo) CreateWriter(ModelRegistryService registry)
    {
        var repo = new FakeModelRouteRepository();
        var writer = new ModelRegistryWriter(
            registry,
            new RegistryGate(),
            new StubScopeFactory(repo),
            new TestUpstreamSecretStore(),
            NullLogger<ModelRegistryWriter>.Instance);
        return (writer, repo);
    }

    private sealed class FakeModelRouteRepository : IModelRouteRepository
    {
        public List<ModelConfig> Saved { get; private set; } = [];

        public Task<IReadOnlyList<ModelConfig>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelConfig>>(Saved);

        public Task ReplaceAllAsync(IReadOnlyList<ModelConfig> models, CancellationToken cancellationToken = default)
        {
            Saved = models.ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class StubScopeFactory(IModelRouteRepository repository)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IModelRouteRepository) ? repository : null;
    }
}
