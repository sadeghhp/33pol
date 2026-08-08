using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Billing;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// Ledger aggregation against a real SQLite engine, where decimals are stored as TEXT.
/// </summary>
/// <remarks>
/// These run on SQLite rather than the in-memory provider on purpose: the in-memory provider keeps
/// decimals as CLR decimals and would happily pass a query that loses precision in production. The
/// figures produced here are one half of billing reconciliation, so a rounding difference introduced
/// at this layer would show up as a permanent, unexplainable discrepancy in the report.
/// </remarks>
public sealed class SqliteBillingLedgerTotalsTests
{
    private static readonly DateOnly Day = new(2026, 3, 14);
    private static readonly DateTimeOffset Noon = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

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

    [Fact]
    public async Task GetDailyTotalsAsync_GroupsByDateTenantModelAndCostCentre()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new BillingEventRepository(db);
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();

        await AppendAsync(repository, "r1", tenant, "gpt-4o", "eng", 10, 5, 0.10m, Noon);
        await AppendAsync(repository, "r2", tenant, "gpt-4o", "eng", 20, 10, 0.20m, Noon.AddHours(1));
        await AppendAsync(repository, "r3", tenant, "gpt-4o", "ops", 5, 1, 0.05m, Noon);
        await AppendAsync(repository, "r4", tenant, "claude", "eng", 7, 2, 0.07m, Noon);
        await AppendAsync(repository, "r5", otherTenant, "gpt-4o", "eng", 3, 1, 0.03m, Noon);
        await AppendAsync(repository, "r6", tenant, "gpt-4o", "eng", 9, 9, 0.09m, Noon.AddDays(1));

        var totals = await repository.GetDailyTotalsAsync(Day, Day);

        totals.Should().HaveCount(4, "the next day's event is outside the window");

        var main = totals.Single(t => t.TenantId == tenant && t.ModelId == "gpt-4o" && t.CostCenter == "eng");
        main.PromptTokens.Should().Be(30);
        main.CompletionTokens.Should().Be(15);
        main.TotalCost.Should().Be(0.30m);
        main.RequestCount.Should().Be(2);
    }

    /// <summary>
    /// The values here sum exactly in decimal but not in IEEE-754 binary floating point. A
    /// server-side SUM() over SQLite's TEXT-stored decimals coerces to REAL and lands on
    /// 0.30000000000000004; this asserts the aggregation stayed in decimal.
    /// </summary>
    [Fact]
    public async Task GetDailyTotalsAsync_SumsCostExactlyInDecimal()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new BillingEventRepository(db);
        var tenant = Guid.NewGuid();

        await AppendAsync(repository, "f1", tenant, "gpt-4o", null, 1, 1, 0.1m, Noon);
        await AppendAsync(repository, "f2", tenant, "gpt-4o", null, 1, 1, 0.2m, Noon);

        var totals = await repository.GetDailyTotalsAsync(Day, Day);

        totals.Should().ContainSingle().Which.TotalCost.Should().Be(0.3m);
        ((double)totals[0].TotalCost).Should().Be(0.3d);
    }

    /// <summary>
    /// Cost is stored at 10 decimal places, so a bucket of small requests must not round to zero on
    /// the way through aggregation.
    /// </summary>
    [Fact]
    public async Task GetDailyTotalsAsync_PreservesTenDecimalPlaceCosts()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new BillingEventRepository(db);
        var tenant = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            await AppendAsync(repository, $"t{i}", tenant, "tiny", null, 1, 1, 0.0000000001m, Noon);
        }

        var totals = await repository.GetDailyTotalsAsync(Day, Day);

        totals.Should().ContainSingle().Which.TotalCost.Should().Be(0.0000000005m);
    }

    [Fact]
    public async Task GetDailyTotalsAsync_TreatsBlankCostCentreAsNull()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new BillingEventRepository(db);
        var tenant = Guid.NewGuid();

        await AppendAsync(repository, "b1", tenant, "gpt-4o", null, 1, 1, 0.01m, Noon);
        await AppendAsync(repository, "b2", tenant, "gpt-4o", "   ", 1, 1, 0.01m, Noon);

        var totals = await repository.GetDailyTotalsAsync(Day, Day);

        // DailyUsageRollupKey normalises blank to null, so the rollup writer produces one bucket
        // here. Producing two would report a permanent false discrepancy against it.
        totals.Should().ContainSingle().Which.CostCenter.Should().BeNull();
        totals[0].RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task GetDailyTotalsAsync_TreatsUnpricedEventsAsZeroCostButStillCountsThem()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new BillingEventRepository(db);
        var tenant = Guid.NewGuid();

        await AppendAsync(repository, "u1", tenant, "unpriced", null, 10, 5, null, Noon);

        var totals = await repository.GetDailyTotalsAsync(Day, Day);

        var bucket = totals.Should().ContainSingle().Subject;
        bucket.TotalCost.Should().Be(0m);
        bucket.PromptTokens.Should().Be(10);
        bucket.RequestCount.Should().Be(1, "an unpriced model still consumed tokens");
    }

    /// <summary>The window is inclusive at both ends, in UTC.</summary>
    [Fact]
    public async Task GetDailyTotalsAsync_IncludesBothBoundaryDaysEndToEnd()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new BillingEventRepository(db);
        var tenant = Guid.NewGuid();

        var firstInstant = new DateTimeOffset(2026, 3, 13, 0, 0, 0, TimeSpan.Zero);
        var lastInstant = new DateTimeOffset(2026, 3, 14, 23, 59, 59, TimeSpan.Zero);
        await AppendAsync(repository, "e1", tenant, "gpt-4o", null, 1, 1, 0.01m, firstInstant);
        await AppendAsync(repository, "e2", tenant, "gpt-4o", null, 1, 1, 0.01m, lastInstant);
        await AppendAsync(repository, "e3", tenant, "gpt-4o", null, 1, 1, 0.01m, firstInstant.AddSeconds(-1));

        var totals = await repository.GetDailyTotalsAsync(new DateOnly(2026, 3, 13), Day);

        totals.Should().HaveCount(2);
        totals.Sum(t => t.RequestCount).Should().Be(2, "the event a second before the window is excluded");
    }

    [Fact]
    public async Task GetDailyTotalsAsync_WhenWindowIsInverted_ReturnsEmpty()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new BillingEventRepository(db);

        await AppendAsync(repository, "i1", Guid.NewGuid(), "gpt-4o", null, 1, 1, 0.01m, Noon);

        var totals = await repository.GetDailyTotalsAsync(Day, Day.AddDays(-1));

        totals.Should().BeEmpty();
    }

    private static Task AppendAsync(
        BillingEventRepository repository,
        string requestId,
        Guid tenantId,
        string modelId,
        string? costCenter,
        long promptTokens,
        long completionTokens,
        decimal? totalCost,
        DateTimeOffset recordedAt) =>
        repository.TryAppendAsync(new BillingEventRecord(
            Guid.NewGuid(),
            requestId,
            tenantId,
            Guid.NewGuid(),
            modelId,
            costCenter,
            promptTokens,
            completionTokens,
            null,
            null,
            totalCost,
            100,
            recordedAt));
}
