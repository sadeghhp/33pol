using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Persistence;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class CachingRateCardRepositoryTests
{
    private static CachingRateCardRepository Create(GatewayDbContext db, IMemoryCache cache, int ttlSeconds = 60) =>
        new(
            new RateCardRepository(db),
            cache,
            Options.Create(new BillingOptions { RateCardCacheTtlSeconds = ttlSeconds }));

    [Fact]
    public async Task GetActiveForModelAsync_SecondCall_DoesNotHitDatabase()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetActiveForModelAsync_SecondCall_DoesNotHitDatabase));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);
        (await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow))!.InputPricePerMillionTokens.Should().Be(3m);

        // Mutate underneath the cache; a cached read must not see it.
        var entity = db.RateCards.Single(r => r.ModelId == "gpt-4o");
        entity.InputPricePerMillionTokens = 99m;
        await db.SaveChangesAsync();

        var cached = await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow);
        cached!.InputPricePerMillionTokens.Should().Be(3m);
    }

    [Fact]
    public async Task UpsertForModelAsync_InvalidatesCache()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(UpsertForModelAsync_InvalidatesCache));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);
        await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow);

        await repository.UpsertForModelAsync("gpt-4o", 7m, 21m);

        var reread = await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow);
        reread!.InputPricePerMillionTokens.Should().Be(7m);
    }

    [Fact]
    public async Task DeleteForModelAsync_InvalidatesCache()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(DeleteForModelAsync_InvalidatesCache));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);
        await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow);

        await repository.DeleteForModelAsync("gpt-4o");

        (await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow)).Should().BeNull();
    }

    [Fact]
    public async Task UnpricedModel_IsCached_SoRepeatLookupsDoNotQuery()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(UnpricedModel_IsCached_SoRepeatLookupsDoNotQuery));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        (await repository.GetActiveForModelAsync("unpriced", DateTimeOffset.UtcNow)).Should().BeNull();

        // Insert directly, bypassing the decorator so no invalidation happens.
        await new RateCardRepository(db).UpsertForModelAsync("unpriced", 1m, 2m);

        // The cached "no rate card" answer is still served.
        (await repository.GetActiveForModelAsync("unpriced", DateTimeOffset.UtcNow)).Should().BeNull();
    }

    [Fact]
    public async Task HistoricalLookup_BypassesCache()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(HistoricalLookup_BypassesCache));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);
        await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow);

        // Well outside the TTL window: must be answered from the database, where the rate card
        // did not yet exist, rather than from the cached current price.
        var historical = await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow.AddDays(-30));
        historical.Should().BeNull();
    }
}
