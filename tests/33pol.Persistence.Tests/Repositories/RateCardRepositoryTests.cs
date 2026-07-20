using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class RateCardRepositoryTests
{
    [Fact]
    public async Task UpsertForModelAsync_InsertsWhenModelUnpriced()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(UpsertForModelAsync_InsertsWhenModelUnpriced));
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);

        var stored = await repository.GetForModelAsync("gpt-4o");
        stored.Should().NotBeNull();
        stored!.InputPricePerMillionTokens.Should().Be(3m);
        stored.OutputPricePerMillionTokens.Should().Be(15m);
        stored.Currency.Should().Be("USD");
        stored.IsActive.Should().BeTrue();
        stored.EffectiveUntil.Should().BeNull();
    }

    [Fact]
    public async Task UpsertForModelAsync_UpdatesInPlace_DoesNotCreateSecondRow()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(UpsertForModelAsync_UpdatesInPlace_DoesNotCreateSecondRow));
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);
        await repository.UpsertForModelAsync("gpt-4o", 5m, 20m);

        db.RateCards.Count(r => r.ModelId == "gpt-4o").Should().Be(1);

        var stored = await repository.GetForModelAsync("gpt-4o");
        stored!.InputPricePerMillionTokens.Should().Be(5m);
        stored.OutputPricePerMillionTokens.Should().Be(20m);
    }

    [Fact]
    public async Task UpsertForModelAsync_ModelIdsThatSlugifyIdentically_GetDistinctSlugs()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(UpsertForModelAsync_ModelIdsThatSlugifyIdentically_GetDistinctSlugs));
        var repository = new RateCardRepository(db);

        // Both slugify to "openai-gpt-4o"; Slug carries a unique index.
        await repository.UpsertForModelAsync("openai/gpt-4o", 1m, 2m);
        await repository.UpsertForModelAsync("openai:gpt/4o", 3m, 4m);

        var slugs = db.RateCards.Select(r => r.Slug).ToList();
        slugs.Should().HaveCount(2);
        slugs.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetForModelAsync_ReturnsNull_WhenModelUnpriced()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetForModelAsync_ReturnsNull_WhenModelUnpriced));
        var repository = new RateCardRepository(db);

        (await repository.GetForModelAsync("never-priced")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteForModelAsync_RemovesPricing_AndIsSafeWhenAbsent()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(DeleteForModelAsync_RemovesPricing_AndIsSafeWhenAbsent));
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);
        await repository.DeleteForModelAsync("gpt-4o");

        (await repository.GetForModelAsync("gpt-4o")).Should().BeNull();

        // Second delete must not throw.
        await repository.DeleteForModelAsync("gpt-4o");
    }

    [Fact]
    public async Task GetActiveByModelAsync_ReturnsCurrentPricePerModel()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetActiveByModelAsync_ReturnsCurrentPricePerModel));
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("model-a", 1m, 2m);
        await repository.UpsertForModelAsync("model-b", 3m, 4m);

        var byModel = await repository.GetActiveByModelAsync();

        byModel.Should().HaveCount(2);
        byModel["model-a"].InputPricePerMillionTokens.Should().Be(1m);
        byModel["model-b"].OutputPricePerMillionTokens.Should().Be(4m);
    }

    [Fact]
    public async Task GetActiveForModelAsync_StillHonoursEffectiveWindow()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetActiveForModelAsync_StillHonoursEffectiveWindow));
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);

        // The upsert is effective from "now", so a lookup in the past must not see it.
        var before = await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow.AddDays(-1));
        before.Should().BeNull();

        var after = await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow.AddMinutes(1));
        after.Should().NotBeNull();
    }
}
