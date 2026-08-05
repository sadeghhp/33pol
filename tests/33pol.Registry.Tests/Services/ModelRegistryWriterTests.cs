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
        var (writer, registry, repo) = CreateWriter(
            new ModelConfig { Id = "keep", Url = "http://keep", Aliases = [] },
            new ModelConfig { Id = "remove-me", Url = "http://remove", Aliases = [] });

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

    /// <summary>
    /// A name that is only an alias is not an existing model. Reporting "already exists" sent
    /// operators looking for a model that the list does not contain and cannot be deleted.
    /// </summary>
    [Fact]
    public async Task AddModelAsync_IdMatchingAnotherModelsAlias_NamesTheAliasOwner()
    {
        var (writer, _, _) = CreateWriter(
            new ModelConfig { Id = "owner", Url = "http://owner", Aliases = ["taken-name"] });

        var result = await writer.AddModelAsync(new ModelConfig
        {
            Id = "taken-name",
            Url = "http://new:8000",
            Aliases = [],
        });

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(409);
        result.Message.Should().Contain("alias of model 'owner'");
        result.Message.Should().NotContain("already exists");
    }

    /// <summary>
    /// The write that used to poison the database: rejected during the in-memory swap, but only
    /// after the row had been committed.
    /// </summary>
    [Fact]
    public async Task AddModelAsync_AliasCollidingWithExistingAlias_IsRejectedBeforePersisting()
    {
        var (writer, registry, repo) = CreateWriter(
            new ModelConfig { Id = "model-a", Url = "http://a", Aliases = ["shared"] });

        var result = await writer.AddModelAsync(new ModelConfig
        {
            Id = "model-b",
            Url = "http://b",
            Aliases = ["shared"],
        });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("shared");
        repo.Saved.Select(m => m.Id).Should().BeEquivalentTo(["model-a"], "a rejected write must not reach the database");
        repo.WriteCount.Should().Be(0);
        registry.ModelExists("model-b").Should().BeFalse();
    }

    /// <summary>
    /// Removing the last route is a legitimate end state, and refusing it left the operator with a
    /// row they could neither delete nor re-create.
    /// </summary>
    [Fact]
    public async Task RemoveModelAsync_LastModel_EmptiesRegistryAndPersists()
    {
        var (writer, registry, repo) = CreateWriter(
            new ModelConfig { Id = "only", Url = "http://only", Aliases = [] });

        var result = await writer.RemoveModelAsync("only");

        result.Success.Should().BeTrue();
        registry.GetAllModels().Should().BeEmpty();
        registry.IsLoaded.Should().BeTrue("an empty registry is loaded, not broken");
        repo.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveThenAddSameId_Succeeds()
    {
        var (writer, registry, _) = CreateWriter(
            new ModelConfig { Id = "only", Url = "http://only", Aliases = ["only-alias"] });

        (await writer.RemoveModelAsync("only")).Success.Should().BeTrue();

        var readd = await writer.AddModelAsync(new ModelConfig
        {
            Id = "only",
            Url = "http://only-again",
            Aliases = ["only-alias"],
        });

        readd.Success.Should().BeTrue(readd.Message);
        registry.ModelExists("only").Should().BeTrue();
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
            Id = "seed",
            Url = "http://updated:9000",
            Aliases = ["updated-alias"],
        });

        result.Success.Should().BeTrue();
        registry.TryGetModel("updated-alias", out var model).Should().BeTrue();
        model!.Url.Should().Be("http://updated:9000");
        repo.Saved.Should().ContainSingle(m => m.Id == "seed" && m.Url == "http://updated:9000");
    }

    [Fact]
    public async Task UpdateModelAsync_BlankBodyId_KeepsExistingId()
    {
        var (writer, registry, _) = CreateWriterWithSeed();

        var result = await writer.UpdateModelAsync("seed", new ModelConfig
        {
            Id = "",
            Url = "http://updated:9000",
            Aliases = [],
        });

        result.Success.Should().BeTrue();
        registry.ModelExists("seed").Should().BeTrue();
    }

    /// <summary>
    /// A rename used to report success while silently keeping the old id, so the operator's change
    /// simply vanished.
    /// </summary>
    [Fact]
    public async Task UpdateModelAsync_DifferentBodyId_RenamesModel()
    {
        var (writer, registry, repo) = CreateWriterWithSeed();

        var result = await writer.UpdateModelAsync("seed", new ModelConfig
        {
            Id = "renamed",
            Url = "http://seed",
            Aliases = [],
        });

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("renamed");
        registry.ModelExists("renamed").Should().BeTrue();
        registry.ModelExists("seed").Should().BeFalse();
        repo.Saved.Select(m => m.Id).Should().BeEquivalentTo(["renamed"]);
    }

    [Fact]
    public async Task UpdateModelAsync_RenameOntoExistingId_Returns409()
    {
        var (writer, registry, _) = CreateWriter(
            new ModelConfig { Id = "first", Url = "http://first", Aliases = [] },
            new ModelConfig { Id = "second", Url = "http://second", Aliases = [] });

        var result = await writer.UpdateModelAsync("first", new ModelConfig
        {
            Id = "second",
            Url = "http://first",
            Aliases = [],
        });

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(409);
        registry.ModelExists("first").Should().BeTrue();
    }

    /// <summary>
    /// The route table is rewritten wholesale, so a write based on a stale read would delete every
    /// route the other writer had just added.
    /// </summary>
    [Fact]
    public async Task Mutation_WhenRoutesChangedConcurrently_Returns409AndKeepsBothWritersRoutes()
    {
        var (writer, _, repo) = CreateWriterWithSeed();
        repo.OnRead = () => repo.SimulateExternalWrite(
            new ModelConfig { Id = "seed", Url = "http://seed", Aliases = [] },
            new ModelConfig { Id = "added-elsewhere", Url = "http://elsewhere", Aliases = [] });

        var result = await writer.AddModelAsync(new ModelConfig
        {
            Id = "mine",
            Url = "http://mine",
            Aliases = [],
        });

        result.Success.Should().BeFalse();
        result.SuggestedStatusCode.Should().Be(409);
        result.Message.Should().Contain("try again");
        repo.Saved.Select(m => m.Id).Should().Contain("added-elsewhere");
    }

    /// <summary>
    /// Memory is a per-process cache; the durable table is the truth a mutation must be built on.
    /// </summary>
    [Fact]
    public async Task AddModelAsync_WithStaleInMemoryRegistry_DoesNotDropRoutesItCannotSee()
    {
        var (writer, registry, repo) = CreateWriter(
            new ModelConfig { Id = "known", Url = "http://known", Aliases = [] });

        // Another replica added a route this process has not reloaded yet.
        repo.SimulateExternalWrite(
            new ModelConfig { Id = "known", Url = "http://known", Aliases = [] },
            new ModelConfig { Id = "invisible-here", Url = "http://elsewhere", Aliases = [] });

        var result = await writer.AddModelAsync(new ModelConfig
        {
            Id = "mine",
            Url = "http://mine",
            Aliases = [],
        });

        result.Success.Should().BeTrue(result.Message);
        repo.Saved.Select(m => m.Id).Should().BeEquivalentTo(["known", "invisible-here", "mine"]);
        registry.ModelExists("invisible-here").Should().BeTrue("the write refreshes memory from the database");
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

    private static (ModelRegistryWriter Writer, ModelRegistryService Registry, FakeModelRouteRepository Repo) CreateWriterWithSeed() =>
        CreateWriter(new ModelConfig { Id = "seed", Url = "http://seed", Aliases = [] });

    private static (ModelRegistryWriter Writer, ModelRegistryService Registry, FakeModelRouteRepository Repo) CreateWriter(
        params ModelConfig[] seed)
    {
        var registry = new ModelRegistryService(NullLogger<ModelRegistryService>.Instance);
        var repo = new FakeModelRouteRepository();
        if (seed.Length > 0)
        {
            repo.SimulateExternalWrite(seed);
            registry.Apply(seed, repo.Version);
        }
        else
        {
            registry.Apply([]);
        }

        var writer = new ModelRegistryWriter(
            registry,
            new RegistryGate(),
            new StubScopeFactory(repo),
            new TestUpstreamSecretStore(),
            NullLogger<ModelRegistryWriter>.Instance);
        return (writer, registry, repo);
    }

    private sealed class FakeModelRouteRepository : IModelRouteRepository
    {
        public List<ModelConfig> Saved { get; private set; } = [];

        public long Version { get; private set; }

        public int WriteCount { get; private set; }

        /// <summary>Hook to simulate another writer landing between this caller's read and its write.</summary>
        public Action? OnRead { get; set; }

        /// <summary>Applies a change as if it came from another process: content plus a version bump.</summary>
        public void SimulateExternalWrite(params ModelConfig[] models)
        {
            Saved = models.ToList();
            Version++;
        }

        public Task<IReadOnlyList<ModelConfig>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelConfig>>(Saved);

        public Task<ModelRouteSnapshot> ListWithVersionAsync(CancellationToken cancellationToken = default)
        {
            var snapshot = new ModelRouteSnapshot(Saved.ToList(), Version);
            OnRead?.Invoke();
            return Task.FromResult(snapshot);
        }

        public Task<long> GetVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Version);

        public Task<long> ReplaceAllAsync(
            IReadOnlyList<ModelConfig> models,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            if (expectedVersion is long expected && expected != Version)
            {
                throw new ModelRouteVersionConflictException(expected, Version);
            }

            Saved = models.ToList();
            Version++;
            WriteCount++;
            return Task.FromResult(Version);
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
