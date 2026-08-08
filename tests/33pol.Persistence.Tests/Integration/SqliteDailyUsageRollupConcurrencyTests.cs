using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pol33.Core.Billing;
using Pol33.Persistence;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// Daily rollups are the billing totals operators and invoices read. Applying them as a
/// read-modify-write of an absolute total meant two overlapping writers could both read the same
/// starting value and one's tokens and cost would vanish. These tests run against a real SQLite
/// engine, since the InMemory provider models neither transactions nor row locking.
/// </summary>
public sealed class SqliteDailyUsageRollupConcurrencyTests
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

    private static DailyUsageRollupDelta Delta(
        DateOnly usageDate,
        Guid tenantId,
        long promptTokens = 1,
        long completionTokens = 1,
        decimal cost = 0.5m,
        int requestCount = 1,
        string modelId = "gpt-4o",
        string? costCenter = "cc") =>
        new(usageDate, tenantId, modelId, costCenter, promptTokens, completionTokens, cost, requestCount);

    /// <summary>
    /// The mechanism that makes concurrent increments safe: the read and the write happen inside one
    /// serializable, <em>immediate</em> transaction, which takes SQLite's write lock before the read,
    /// so a second writer cannot observe a stale starting value and overwrite the first writer's usage.
    /// </summary>
    /// <remarks>
    /// Asserted directly rather than only through outcomes: SQLite's own write serialization masks
    /// the lost update often enough that a purely statistical concurrency test passes even without
    /// the transaction, so it cannot on its own demonstrate the fix.
    /// </remarks>
    [Fact]
    public async Task IncrementRollupsAsync_UsesASerializableTransaction()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        var interceptor = new TransactionRecordingInterceptor();
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new GatewayDbContext(options);
        await new DailyUsageRollupRepository(db).IncrementRollupsAsync(
            [Delta(DateOnly.FromDateTime(DateTime.UtcNow), Guid.NewGuid())]);

        interceptor.StartedIsolationLevels
            .Should().ContainSingle()
            .Which.Should().Be(System.Data.IsolationLevel.Serializable);
    }

    /// <summary>
    /// Pins the platform behaviour the rollup path depends on: only <c>deferred: false</c> takes the
    /// write lock at BEGIN.
    /// </summary>
    /// <remarks>
    /// EF's <c>BeginTransactionAsync(IsolationLevel.Serializable)</c> goes through the overload that
    /// leaves the transaction deferred, so it does <em>not</em> give this guarantee — which is why
    /// the repository begins its transaction on the raw connection instead. A deferred transaction
    /// takes only a shared lock for its read and must upgrade to write later; under WAL that upgrade
    /// fails with <c>SQLITE_BUSY_SNAPSHOT</c>, which <c>busy_timeout</c> does not retry because the
    /// snapshot is genuinely stale — surfacing as a whole batch of rollups silently going missing.
    /// </remarks>
    [Fact]
    public async Task ImmediateTransaction_HoldsTheWriteLockFromBegin()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var first = new SqliteConnection(connectionString);
        await first.OpenAsync();
        await using var firstTransaction =
            first.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);

        await using var second = new SqliteConnection(connectionString);
        await second.OpenAsync();

        var competing = () => second.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: false);

        competing.Should().Throw<SqliteException>("an immediate transaction holds the write lock from BEGIN");

        // A deferred BEGIN takes no lock, so it succeeds — the very difference that made the
        // repository's read-then-write racy.
        await using var deferredTransaction =
            second.BeginTransaction(System.Data.IsolationLevel.Serializable, deferred: true);
        deferredTransaction.Should().NotBeNull();
    }

    /// <summary>
    /// UpsertRollupsAsync (absolute totals) is the legacy path and deliberately keeps its
    /// non-transactional behaviour; only the additive path carries the concurrency guarantee.
    /// </summary>
    [Fact]
    public async Task UpsertRollupsAsync_DoesNotOpenItsOwnTransaction()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        var interceptor = new TransactionRecordingInterceptor();
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new GatewayDbContext(options);
        await new DailyUsageRollupRepository(db).UpsertRollupsAsync([
            new DailyUsageRollupRecord(
                DateOnly.FromDateTime(DateTime.UtcNow), Guid.NewGuid(), "gpt-4o", "cc", 1, 1, 1m, 1),
        ]);

        interceptor.StartedIsolationLevels.Should().BeEmpty();
    }

    /// <summary>
    /// Outcome-level cover for the same property: many writers incrementing one bucket must produce
    /// totals equal to the sum of every increment.
    /// </summary>
    [Fact]
    public async Task IncrementRollupsAsync_ConcurrentWriters_LoseNoIncrements()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        var tenantId = Guid.NewGuid();
        var usageDate = DateOnly.FromDateTime(DateTime.UtcNow);

        const int writers = 8;
        const int perWriter = 25;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(async _ =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                // A fresh context per operation, as each scoped flush gets in production.
                await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
                var repository = new DailyUsageRollupRepository(db);
                await repository.IncrementRollupsAsync([Delta(usageDate, tenantId)]);
            }
        }));

        await using var verify = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var rollups = await new DailyUsageRollupRepository(verify)
            .GetRollupsAsync(usageDate, usageDate, tenantId);

        var total = rollups.Should().ContainSingle().Subject;
        const int expectedOperations = writers * perWriter;
        total.RequestCount.Should().Be(expectedOperations);
        total.PromptTokens.Should().Be(expectedOperations);
        total.CompletionTokens.Should().Be(expectedOperations);
        total.TotalCost.Should().Be(0.5m * expectedOperations);
    }

    [Fact]
    public async Task IncrementRollupsAsync_CreatesTheBucketOnFirstUse()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        var tenantId = Guid.NewGuid();
        var usageDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new DailyUsageRollupRepository(db);

        await repository.IncrementRollupsAsync([Delta(usageDate, tenantId, 10, 20, 3m, 1)]);

        var rollup = (await repository.GetRollupsAsync(usageDate, usageDate, tenantId)).Should().ContainSingle().Subject;
        rollup.PromptTokens.Should().Be(10);
        rollup.CompletionTokens.Should().Be(20);
        rollup.TotalCost.Should().Be(3m);
        rollup.RequestCount.Should().Be(1);
    }

    /// <summary>
    /// Two deltas for the same bucket inside one call must accumulate onto a single row rather than
    /// attempting a second insert, which the unique index would reject.
    /// </summary>
    [Fact]
    public async Task IncrementRollupsAsync_DuplicateBucketsInOneCall_AccumulateOntoOneRow()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        var tenantId = Guid.NewGuid();
        var usageDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new DailyUsageRollupRepository(db);

        await repository.IncrementRollupsAsync([
            Delta(usageDate, tenantId, 1, 1, 1m),
            Delta(usageDate, tenantId, 2, 2, 2m),
        ]);

        var rollup = (await repository.GetRollupsAsync(usageDate, usageDate, tenantId)).Should().ContainSingle().Subject;
        rollup.PromptTokens.Should().Be(3);
        rollup.CompletionTokens.Should().Be(3);
        rollup.TotalCost.Should().Be(3m);
        rollup.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task IncrementRollupsAsync_DistinctBuckets_StayIsolated()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var usageDate = DateOnly.FromDateTime(DateTime.UtcNow);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new DailyUsageRollupRepository(db);

        await repository.IncrementRollupsAsync([
            Delta(usageDate, tenantA, 1, 1, 1m, modelId: "gpt-4o", costCenter: "cc-1"),
            Delta(usageDate, tenantA, 2, 2, 2m, modelId: "gpt-4o-mini", costCenter: "cc-1"),
            Delta(usageDate, tenantA, 4, 4, 4m, modelId: "gpt-4o", costCenter: "cc-2"),
            Delta(usageDate, tenantB, 8, 8, 8m, modelId: "gpt-4o", costCenter: "cc-1"),
        ]);

        (await repository.GetRollupsAsync(usageDate, usageDate, tenantA)).Should().HaveCount(3);

        var tenantBRollup = (await repository.GetRollupsAsync(usageDate, usageDate, tenantB))
            .Should().ContainSingle().Subject;
        tenantBRollup.PromptTokens.Should().Be(8);
    }

    /// <summary>
    /// Concurrent writers targeting different buckets must all land, and must not interfere.
    /// </summary>
    [Fact]
    public async Task IncrementRollupsAsync_ConcurrentDistinctBuckets_AllLand()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        var usageDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var tenants = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(tenants.Select(async tenantId =>
        {
            for (var i = 0; i < 10; i++)
            {
                await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
                await new DailyUsageRollupRepository(db).IncrementRollupsAsync([Delta(usageDate, tenantId)]);
            }
        }));

        await using var verify = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new DailyUsageRollupRepository(verify);

        foreach (var tenantId in tenants)
        {
            var rollup = (await repository.GetRollupsAsync(usageDate, usageDate, tenantId))
                .Should().ContainSingle().Subject;
            rollup.RequestCount.Should().Be(10);
        }
    }

    /// <summary>Records the isolation level of every transaction EF opens on the context.</summary>
    private sealed class TransactionRecordingInterceptor : DbTransactionInterceptor
    {
        private readonly List<System.Data.IsolationLevel> _levels = [];

        public IReadOnlyList<System.Data.IsolationLevel> StartedIsolationLevels => _levels;

        public override ValueTask<System.Data.Common.DbTransaction> TransactionStartedAsync(
            System.Data.Common.DbConnection connection,
            TransactionEndEventData eventData,
            System.Data.Common.DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            _levels.Add(result.IsolationLevel);
            return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
        }

        /// <summary>
        /// The rollup path begins its transaction on the raw <c>SqliteConnection</c> so it can pass
        /// <c>deferred: false</c> — EF's own BeginTransaction(IsolationLevel) overload leaves the
        /// transaction deferred — and then enrolls it via UseTransaction. EF reports that as a
        /// transaction being <em>used</em>, so both hooks count.
        /// </summary>
        public override ValueTask<System.Data.Common.DbTransaction> TransactionUsedAsync(
            System.Data.Common.DbConnection connection,
            TransactionEventData eventData,
            System.Data.Common.DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            if (result is not null)
            {
                _levels.Add(result.IsolationLevel);
            }

            return base.TransactionUsedAsync(connection, eventData, result!, cancellationToken);
        }
    }
}
