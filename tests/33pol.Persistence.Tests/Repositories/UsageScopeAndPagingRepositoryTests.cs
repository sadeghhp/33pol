using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Billing;
using Pol33.Core.Identity;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

/// <summary>
/// The Usage &amp; cost page's repository contract: tenant scope with optional anonymous rows,
/// case-insensitive cost-centre matching, keyset paging and ledger aggregation. Runs on real SQLite
/// so the LOWER() translation and tick comparisons are the ones Production executes.
/// </summary>
public sealed class UsageScopeAndPagingRepositoryTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OtherTenant = Guid.NewGuid();
    private static readonly Guid KeyA = Guid.NewGuid();
    private static readonly Guid KeyB = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 6, 10);
    private static readonly DateTimeOffset Noon = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static string NewConnectionString() => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    private static BillingEventRecord Event(
        string requestId, Guid? tenant, Guid? key, string? costCenter, DateTimeOffset at, decimal? cost = 0.01m) =>
        new(Guid.NewGuid(), requestId, tenant, key, "gpt-4o", costCenter, 10, 5, null, null, cost, 10, at);

    private static async Task SeedLedgerAsync(BillingEventRepository sut)
    {
        await sut.TryAppendManyAsync(
        [
            Event("t-1", Tenant, KeyA, "Engineering", Noon),
            Event("t-2", Tenant, KeyA, "engineering", Noon.AddSeconds(1)),
            Event("t-3", Tenant, KeyB, null, Noon.AddSeconds(2)),
            Event("o-1", OtherTenant, null, "engineering", Noon.AddSeconds(3)),
            Event("anon-1", null, null, null, Noon.AddSeconds(4)),
        ]);
    }

    [Fact]
    public async Task QueryAsync_TenantScope_ExcludesAnonymousUnlessRequested()
    {
        var cs = NewConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(cs);
        await db.Database.MigrateAsync();
        var sut = new BillingEventRepository(db);
        await SeedLedgerAsync(sut);

        var scoped = await sut.QueryAsync(new BillingEventQuery(TenantId: Tenant));
        var withAnon = await sut.QueryAsync(new BillingEventQuery(TenantId: Tenant, IncludeAnonymous: true));

        scoped.Select(e => e.RequestId).Should().BeEquivalentTo("t-1", "t-2", "t-3");
        withAnon.Select(e => e.RequestId).Should().BeEquivalentTo("t-1", "t-2", "t-3", "anon-1");
        withAnon.Should().NotContain(e => e.TenantId == OtherTenant);
    }

    [Fact]
    public async Task QueryAsync_CostCenter_IsCaseInsensitive_AndNoneSelectsNulls()
    {
        var cs = NewConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(cs);
        await db.Database.MigrateAsync();
        var sut = new BillingEventRepository(db);
        await SeedLedgerAsync(sut);

        var eng = await sut.QueryAsync(new BillingEventQuery(TenantId: Tenant, CostCenter: "ENGINEERING"));
        var none = await sut.QueryAsync(new BillingEventQuery(TenantId: Tenant, NoCostCenter: true));

        eng.Select(e => e.RequestId).Should().BeEquivalentTo("t-1", "t-2");
        none.Select(e => e.RequestId).Should().BeEquivalentTo("t-3");
    }

    [Fact]
    public async Task QueryAsync_Cursor_PagesNewestFirstWithoutRepeatingOrSkippingTies()
    {
        var cs = NewConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(cs);
        await db.Database.MigrateAsync();
        var sut = new BillingEventRepository(db);

        // Five rows, three of them sharing one timestamp, so every page boundary lands on a tie.
        var tie = Noon.AddMinutes(1);
        await sut.TryAppendManyAsync(
        [
            Event("r-1", Tenant, KeyA, null, tie.AddSeconds(5)),
            Event("r-2", Tenant, KeyA, null, tie),
            Event("r-3", Tenant, KeyA, null, tie),
            Event("r-4", Tenant, KeyA, null, tie),
            Event("r-5", Tenant, KeyA, null, tie.AddSeconds(-5)),
        ]);

        var seen = new List<string>();
        BillingEventCursor? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var rows = await sut.QueryAsync(new BillingEventQuery(TenantId: Tenant, Limit: 2, Cursor: cursor));
            if (rows.Count == 0)
            {
                break;
            }

            seen.AddRange(rows.Select(r => r.RequestId));
            cursor = BillingEventCursor.After(rows, cursor);
        }

        seen.Should().HaveCount(5).And.OnlyHaveUniqueItems();
        seen[0].Should().Be("r-1");
        seen[^1].Should().Be("r-5");
    }

    [Fact]
    public async Task AggregateDailyAsync_BucketsByDayModelAndCostCenter_ForOneKey()
    {
        var cs = NewConnectionString();
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(cs);
        await db.Database.MigrateAsync();
        var sut = new BillingEventRepository(db);
        await SeedLedgerAsync(sut);

        var buckets = await sut.AggregateDailyAsync(
            new BillingEventQuery(Day, Day, Tenant, KeyA));

        // "Engineering" and "engineering" are distinct buckets in the ledger (the rollup writer keys
        // by the trimmed, case-preserved value); both belong to key A.
        buckets.Should().HaveCount(2);
        buckets.Sum(b => b.RequestCount).Should().Be(2);
        buckets.Should().OnlyContain(b => b.UsageDate == Day && b.ModelId == "gpt-4o" && b.TenantId == Tenant);
    }

    [Fact]
    public async Task GetScopedRollupsAsync_IncludesAnonymousRowsOnlyWhenAsked()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(GetScopedRollupsAsync_IncludesAnonymousRowsOnlyWhenAsked));
        var sut = new DailyUsageRollupRepository(db);
        await sut.UpsertRollupsAsync(
        [
            new DailyUsageRollupRecord(Day, Tenant, "gpt-4o", "eng", 10, 5, 0.10m, 1),
            new DailyUsageRollupRecord(Day, OtherTenant, "gpt-4o", "eng", 10, 5, 0.10m, 1),
            new DailyUsageRollupRecord(Day, null, "gpt-4o", null, 10, 5, 0.10m, 1),
        ]);

        var scoped = await sut.GetScopedRollupsAsync(new UsageScope(Tenant), Day, Day);
        var withAnon = await sut.GetScopedRollupsAsync(new UsageScope(Tenant, IncludeAnonymous: true), Day, Day);
        var everything = await sut.GetScopedRollupsAsync(UsageScope.Unrestricted, Day, Day);

        scoped.Should().ContainSingle().Which.TenantId.Should().Be(Tenant);
        withAnon.Select(r => r.TenantId).Should().BeEquivalentTo(new Guid?[] { Tenant, null });
        everything.Should().HaveCount(3);
    }

    [Fact]
    public async Task ApiKeyRepository_GetByIdsAsync_ReturnsOnlyExistingKeys()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(ApiKeyRepository_GetByIdsAsync_ReturnsOnlyExistingKeys));
        var tenants = new TenantRepository(db);
        var sut = new ApiKeyRepository(db);
        var now = DateTimeOffset.UtcNow;
        await tenants.CreateAsync(new TenantRecord(Tenant, "t", "T", null, null, true, now, now));
        var a = await sut.CreateAsync(new ApiKeyRecord(Guid.NewGuid(), Tenant, "h1", "sk-33pol-aaa", ApiKeyRole.Inference, ["inference"], null, null, now, null));
        var b = await sut.CreateAsync(new ApiKeyRecord(Guid.NewGuid(), Tenant, "h2", "sk-33pol-bbb", ApiKeyRole.Inference, ["inference"], null, null, now, null));

        var found = await sut.GetByIdsAsync([a.Id, b.Id, Guid.NewGuid()]);

        found.Select(k => k.Id).Should().BeEquivalentTo(new[] { a.Id, b.Id });
        (await sut.GetByIdsAsync([])).Should().BeEmpty();
    }
}
