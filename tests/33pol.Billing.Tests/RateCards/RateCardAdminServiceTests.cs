using Pol33.Billing.RateCards;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.RateCards;

public sealed class RateCardAdminServiceTests
{
    private static FakeModelRegistry KnownRegistry() => new("gpt-4o");

    [Fact]
    public async Task SetPricingAsync_CanonicalisesModelId()
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository, KnownRegistry());

        // The registry resolves ids case-insensitively, so pricing must be stored under the
        // canonical id or it would never be found for the model it was meant for.
        var result = await service.SetPricingAsync("GPT-4O", new ModelPricing
        {
            InputPricePerMillionTokens = 3m,
            OutputPricePerMillionTokens = 15m,
        });

        result.Success.Should().BeTrue();
        repository.Upserts[0].ModelId.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task SetPricingAsync_ResolvesAliasToCanonicalId()
    {
        var repository = new FakeRateCardRepository();
        var registry = new FakeModelRegistry("gpt-4o");
        registry.AddAlias("fast", "gpt-4o");
        var service = new RateCardAdminService(repository, registry);

        await service.SetPricingAsync("fast", new ModelPricing
        {
            InputPricePerMillionTokens = 1m,
            OutputPricePerMillionTokens = 2m,
        });

        repository.Upserts[0].ModelId.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task SetPricingAsync_UnknownModel_Returns404_AndWritesNothing()
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository, KnownRegistry());

        var result = await service.SetPricingAsync("typo-model", new ModelPricing
        {
            InputPricePerMillionTokens = 3m,
            OutputPricePerMillionTokens = 15m,
        });

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(404);
        repository.Upserts.Should().BeEmpty("an orphan rate card would silently never apply");
    }

    [Fact]
    public async Task ClearPricingAsync_DoesNotRequireRegistry()
    {
        var repository = new FakeRateCardRepository();
        // Empty registry: clearing runs after a model is deleted, when its id is already gone.
        var service = new RateCardAdminService(repository, new FakeModelRegistry());

        var result = await service.ClearPricingAsync("already-deleted");

        result.Success.Should().BeTrue();
        repository.Deletes.Should().ContainSingle().Which.Should().Be("already-deleted");
    }

    [Fact]
    public async Task SetPricingAsync_PersistsPrices()
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository, KnownRegistry());

        var result = await service.SetPricingAsync("gpt-4o", new ModelPricing
        {
            InputPricePerMillionTokens = 3m,
            OutputPricePerMillionTokens = 15m,
        });

        result.Success.Should().BeTrue();
        repository.Upserts.Should().ContainSingle();
        repository.Upserts[0].Should().Be(("gpt-4o", 3m, 15m));
    }

    [Theory]
    [InlineData(-1, 15)]
    [InlineData(3, -0.01)]
    public async Task SetPricingAsync_RejectsNegativePrices(decimal input, decimal output)
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository, KnownRegistry());

        var result = await service.SetPricingAsync("gpt-4o", new ModelPricing
        {
            InputPricePerMillionTokens = input,
            OutputPricePerMillionTokens = output,
        });

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
        result.Message.Should().Contain("negative");
        repository.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task SetPricingAsync_RejectsPriceBeyondColumnPrecision()
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository, KnownRegistry());

        var result = await service.SetPricingAsync("gpt-4o", new ModelPricing
        {
            InputPricePerMillionTokens = 1_000_000_000_000m,
            OutputPricePerMillionTokens = 1m,
        });

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
        repository.Upserts.Should().BeEmpty();
    }

    [Fact]
    public async Task SetPricingAsync_RoundsToSixDecimalPlaces()
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository, KnownRegistry());

        await service.SetPricingAsync("gpt-4o", new ModelPricing
        {
            InputPricePerMillionTokens = 0.1234567m,
            OutputPricePerMillionTokens = 1m,
        });

        repository.Upserts[0].Input.Should().Be(0.123457m);
    }

    [Fact]
    public async Task SetPricingAsync_RejectsBlankModelId()
    {
        var service = new RateCardAdminService(new FakeRateCardRepository(), KnownRegistry());

        var result = await service.SetPricingAsync("  ", new ModelPricing());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ClearPricingAsync_DeletesForModel()
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository, KnownRegistry());

        var result = await service.ClearPricingAsync("gpt-4o");

        result.Success.Should().BeTrue();
        repository.Deletes.Should().ContainSingle().Which.Should().Be("gpt-4o");
    }

    [Fact]
    public async Task NoOpService_ReportsDatabaseRequired()
    {
        var service = new NoOpRateCardAdminService();

        var result = await service.SetPricingAsync("gpt-4o", new ModelPricing());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        (await service.GetPricingByModelAsync()).Should().BeEmpty();
    }

    /// <summary>Mirrors the real registry's case-insensitive id and alias resolution.</summary>
    private sealed class FakeModelRegistry : IModelRegistry
    {
        private readonly Dictionary<string, ModelConfig> _lookup = new(StringComparer.OrdinalIgnoreCase);

        public FakeModelRegistry(params string[] modelIds)
        {
            foreach (var id in modelIds)
            {
                _lookup[id] = new ModelConfig { Id = id, Url = "https://upstream.test" };
            }
        }

        public void AddAlias(string alias, string canonicalId) => _lookup[alias] = _lookup[canonicalId];

        public bool TryGetModel(string name, out ModelConfig? model) => _lookup.TryGetValue(name, out model);

        public IReadOnlyList<ModelConfig> GetAllModels() => _lookup.Values.Distinct().ToList();

        public bool ModelExists(string name) => _lookup.ContainsKey(name);

        public string? GetBackendUrl(string name) => _lookup.TryGetValue(name, out var m) ? m.Url : null;

        public Task LoadModelsAsync(string configPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeRateCardRepository : IRateCardRepository
    {
        public List<(string ModelId, decimal Input, decimal Output)> Upserts { get; } = [];

        public List<string> Deletes { get; } = [];

        public Task<RateCardRecord?> GetActiveForModelAsync(string modelId, DateTimeOffset atUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<RateCardRecord?>(null);

        public Task<RateCardRecord?> GetForModelAsync(string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RateCardRecord?>(null);

        public Task<IReadOnlyDictionary<string, RateCardRecord>> GetActiveByModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, RateCardRecord>>(new Dictionary<string, RateCardRecord>());

        public Task UpsertForModelAsync(string modelId, decimal inputPricePerMillionTokens, decimal outputPricePerMillionTokens, CancellationToken cancellationToken = default)
        {
            Upserts.Add((modelId, inputPricePerMillionTokens, outputPricePerMillionTokens));
            return Task.CompletedTask;
        }

        public Task DeleteForModelAsync(string modelId, CancellationToken cancellationToken = default)
        {
            Deletes.Add(modelId);
            return Task.CompletedTask;
        }
    }
}
