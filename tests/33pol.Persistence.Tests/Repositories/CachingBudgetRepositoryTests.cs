using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Persistence;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

/// <summary>
/// Budget definitions are read on every inference request by budget enforcement, so they are cached.
/// Only the definitions — spend is always read fresh, and in-flight cost is covered by the
/// reservation ledger, so caching here cannot let a tenant exceed a hard stop.
/// </summary>
public sealed class CachingBudgetRepositoryTests
{
    private static CachingBudgetRepository Create(GatewayDbContext db, IMemoryCache cache, int ttlSeconds = 30) =>
        new(
            new BudgetRepository(db),
            cache,
            Options.Create(new BillingOptions { BudgetCacheTtlSeconds = ttlSeconds }));

    private static BudgetEntity NewBudget(Guid tenantId, decimal limit, string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            AmountLimit = limit,
            Currency = "USD",
            WarningThresholdRatio = 0.8m,
            HardStopEnabled = true,
            PeriodStartDay = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task GetByTenantAsync_SecondCall_DoesNotHitDatabase()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetByTenantAsync_SecondCall_DoesNotHitDatabase));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        var tenantId = Guid.NewGuid();
        db.Budgets.Add(NewBudget(tenantId, 100m, "Monthly"));
        await db.SaveChangesAsync();

        (await repository.GetByTenantAsync(tenantId)).Single().AmountLimit.Should().Be(100m);

        // Mutate underneath the cache; the cached read must not see it.
        db.Budgets.Single(b => b.TenantId == tenantId).AmountLimit = 999m;
        await db.SaveChangesAsync();

        (await repository.GetByTenantAsync(tenantId)).Single().AmountLimit.Should().Be(100m);
    }

    [Fact]
    public async Task Invalidate_ForcesTheNextReadToHitTheDatabase()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(Invalidate_ForcesTheNextReadToHitTheDatabase));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        var tenantId = Guid.NewGuid();
        db.Budgets.Add(NewBudget(tenantId, 100m, "Monthly"));
        await db.SaveChangesAsync();

        (await repository.GetByTenantAsync(tenantId)).Single().AmountLimit.Should().Be(100m);

        db.Budgets.Single(b => b.TenantId == tenantId).AmountLimit = 250m;
        await db.SaveChangesAsync();

        repository.Invalidate(tenantId);

        (await repository.GetByTenantAsync(tenantId)).Single().AmountLimit.Should().Be(250m);
    }

    /// <summary>
    /// Tenants with no budgets are the common case; caching the empty result is what keeps them off
    /// the database entirely.
    /// </summary>
    [Fact]
    public async Task GetByTenantAsync_EmptyResult_IsCached()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetByTenantAsync_EmptyResult_IsCached));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        var tenantId = Guid.NewGuid();
        (await repository.GetByTenantAsync(tenantId)).Should().BeEmpty();

        db.Budgets.Add(NewBudget(tenantId, 50m, "Added later"));
        await db.SaveChangesAsync();

        (await repository.GetByTenantAsync(tenantId)).Should().BeEmpty("the empty result was cached");

        repository.Invalidate(tenantId);
        (await repository.GetByTenantAsync(tenantId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task GetByTenantAsync_DifferentTenants_AreCachedIndependently()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetByTenantAsync_DifferentTenants_AreCachedIndependently));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var repository = Create(db, cache);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        db.Budgets.Add(NewBudget(tenantA, 100m, "A"));
        db.Budgets.Add(NewBudget(tenantB, 200m, "B"));
        await db.SaveChangesAsync();

        (await repository.GetByTenantAsync(tenantA)).Single().AmountLimit.Should().Be(100m);
        (await repository.GetByTenantAsync(tenantB)).Single().AmountLimit.Should().Be(200m);

        repository.Invalidate(tenantA);

        db.Budgets.Single(b => b.TenantId == tenantB).AmountLimit = 999m;
        await db.SaveChangesAsync();

        (await repository.GetByTenantAsync(tenantB)).Single().AmountLimit
            .Should().Be(200m, "invalidating tenant A must not evict tenant B");
    }
}
