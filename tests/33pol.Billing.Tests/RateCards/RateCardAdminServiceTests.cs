using Pol33.Billing.RateCards;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.RateCards;

public sealed class RateCardAdminServiceTests
{
    [Fact]
    public async Task SetPricingAsync_PersistsPrices()
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository);

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
        var service = new RateCardAdminService(repository);

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
        var service = new RateCardAdminService(repository);

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
        var service = new RateCardAdminService(repository);

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
        var service = new RateCardAdminService(new FakeRateCardRepository());

        var result = await service.SetPricingAsync("  ", new ModelPricing());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task ClearPricingAsync_DeletesForModel()
    {
        var repository = new FakeRateCardRepository();
        var service = new RateCardAdminService(repository);

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
