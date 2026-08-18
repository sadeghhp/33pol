using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// Rate-card behaviour against a real SQLite engine. The InMemory provider used by
/// <see cref="Repositories.RateCardRepositoryTests"/> never runs migration DDL, so it cannot
/// exercise the unique slug index, the NOCASE collation on model_id, or decimal precision.
/// </summary>
public sealed class SqliteRateCardTests
{
    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    private static async Task<SqliteConnection> MigratedKeepAliveAsync(string connectionString)
    {
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        await db.Database.MigrateAsync();
        return keepAlive;
    }

    /// <summary>
    /// The model registry resolves ids case-insensitively, so pricing must too. Before the NOCASE
    /// collation this returned null and the second upsert wrote a duplicate row, meaning a price
    /// set under different casing silently never applied.
    /// </summary>
    [Fact]
    public async Task ModelId_MatchesCaseInsensitively_AndDoesNotDuplicate()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("GPT-4o", 3m, 15m);

        var found = await repository.GetForModelAsync("gpt-4o");
        found.Should().NotBeNull("the registry treats 'GPT-4o' and 'gpt-4o' as one model");
        found!.InputPricePerMillionTokens.Should().Be(3m);

        await repository.UpsertForModelAsync("gpt-4O", 9m, 21m);

        db.RateCards.Count().Should().Be(1, "a differently-cased upsert must update, not duplicate");
        (await repository.GetForModelAsync("GPT-4O"))!.InputPricePerMillionTokens.Should().Be(9m);
    }

    [Fact]
    public async Task DeleteForModelAsync_MatchesCaseInsensitively()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("GPT-4o", 3m, 15m);
        await repository.DeleteForModelAsync("gpt-4o");

        db.RateCards.Count().Should().Be(0);
    }

    [Fact]
    public async Task Slug_UniqueIndex_IsEnforced_AndCollisionsAreAvoided()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new RateCardRepository(db);

        // Both slugify to "openai-gpt-4o"; the real unique index would reject a duplicate.
        await repository.UpsertForModelAsync("openai/gpt-4o", 1m, 2m);
        await repository.UpsertForModelAsync("openai:gpt/4o", 3m, 4m);

        var slugs = db.RateCards.Select(r => r.Slug).ToList();
        slugs.Should().HaveCount(2);
        slugs.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Prices_RoundTripAtFullColumnPrecision()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            // decimal(18,6): six decimal places and twelve integer digits.
            await new RateCardRepository(db).UpsertForModelAsync("m", 0.123456m, 999_999.999999m);
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var read = await new RateCardRepository(db).GetForModelAsync("m");
            read!.InputPricePerMillionTokens.Should().Be(0.123456m);
            read.OutputPricePerMillionTokens.Should().Be(999_999.999999m);
        }
    }

    [Fact]
    public async Task GetActiveByModelAsync_TranslatesAndReturnsCurrentPrices()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("model-a", 1m, 2m);
        await repository.UpsertForModelAsync("model-b", 3m, 4m);

        var byModel = await repository.GetActiveByModelAsync();

        byModel.Should().HaveCount(2);
        byModel["model-a"].InputPricePerMillionTokens.Should().Be(1m);
        byModel["model-b"].OutputPricePerMillionTokens.Should().Be(4m);
    }

    /// <summary>
    /// The collation change rebuilds the table, so existing rows must survive it. Applies the
    /// schema as it stood before this migration, inserts a rate card, then migrates to head.
    /// </summary>
    [Fact]
    public async Task NoCaseMigration_PreservesExistingRows_AndMakesThemCaseInsensitive()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260720063255_RateLimitEnabledToggle");
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await new RateCardRepository(db).UpsertForModelAsync("GPT-4o", 3m, 15m);
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var repository = new RateCardRepository(db);

            db.RateCards.Count().Should().Be(1, "the pre-existing row must survive the table rebuild");
            var found = await repository.GetForModelAsync("gpt-4o");
            found.Should().NotBeNull("the migrated column must now match case-insensitively");
            found!.InputPricePerMillionTokens.Should().Be(3m);
        }
    }

    /// <summary>
    /// The cache in front of the repository must fold case the same way the NOCASE column does.
    /// With a raw-cased cache key the two spellings occupied separate entries, so an admin price
    /// change evicted only the casing used on the write path and the other kept serving the stale
    /// price for the rest of its TTL.
    /// </summary>
    [Fact]
    public async Task CachedRepository_DifferentCasing_SharesOneEntryAndInvalidatesTogether()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

        var repository = new CachingRateCardRepository(
            new RateCardRepository(db),
            cache,
            Microsoft.Extensions.Options.Options.Create(
                new Pol33.Core.Configuration.BillingOptions { RateCardCacheTtlSeconds = 60 }));

        await repository.UpsertForModelAsync("gpt-4o", 3m, 15m);

        // Warm the cache under one casing.
        (await repository.GetActiveForModelAsync("GPT-4o", DateTimeOffset.UtcNow))!
            .InputPricePerMillionTokens.Should().Be(3m);

        // Change the price under another casing.
        await repository.UpsertForModelAsync("gpt-4o", 9m, 40m);

        (await repository.GetActiveForModelAsync("GPT-4o", DateTimeOffset.UtcNow))!
            .InputPricePerMillionTokens
            .Should().Be(9m, "the change must be visible whatever casing the reader uses");

        await repository.DeleteForModelAsync("GPT-4O");

        (await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow))
            .Should().BeNull("the deletion must be visible whatever casing the reader uses");
    }

    /// <summary>
    /// The bulk path (admin list) and the single-model path (billing hot path) must agree about
    /// which card is current for a model, whatever casing is used.
    /// </summary>
    [Fact]
    public async Task BulkAndSingleLookup_AgreeAcrossCasing()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new RateCardRepository(db);

        await repository.UpsertForModelAsync("GPT-4o", 3m, 15m);

        var single = await repository.GetActiveForModelAsync("gpt-4o", DateTimeOffset.UtcNow);
        var bulk = await repository.GetActiveByModelAsync();

        single.Should().NotBeNull();
        bulk.TryGetValue("gpt-4o", out var fromBulk).Should().BeTrue();
        fromBulk!.InputPricePerMillionTokens.Should().Be(single!.InputPricePerMillionTokens);
        fromBulk.OutputPricePerMillionTokens.Should().Be(single.OutputPricePerMillionTokens);
    }

    /// <summary>
    /// Two admins pricing a brand-new model at once: without a write lock both saw "no active card"
    /// and either two active cards existed (whichever GetActiveForModelAsync picked won) or the
    /// slug's unique index turned one request into a 500. Under BEGIN IMMEDIATE the second waits and
    /// updates the first's card.
    /// </summary>
    [Fact]
    public async Task UpsertForModel_ConcurrentWritersForANewModel_LeaveExactlyOneActiveCard()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        const int writers = 8;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async i =>
        {
            await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
            await new RateCardRepository(db).UpsertForModelAsync("brand/new-model", 1m + i, 2m + i);
        }));

        await using var verify = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        (await verify.RateCards.AsNoTracking().CountAsync(r => r.ModelId == "brand/new-model")).Should().Be(1);
        (await new RateCardRepository(verify).GetForModelAsync("brand/new-model")).Should().NotBeNull();
    }
}
