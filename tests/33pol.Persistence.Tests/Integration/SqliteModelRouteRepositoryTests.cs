using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pol33.Core.Models;
using Pol33.Persistence;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// Route persistence against a real SQLite engine, where the unique index on model_id and real
/// transactions actually exist. The route table is rewritten wholesale on every change, so the
/// version check is the only thing standing between two concurrent admins and lost routes.
/// </summary>
public sealed class SqliteModelRouteRepositoryTests
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

    private static ModelConfig Model(string id) =>
        new() { Id = id, Url = "http://upstream/" + id, MaxContextLength = 8192 };

    [Fact]
    public async Task ReplaceAll_BumpsVersion_AndRoundTripsRoutes()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new ModelRouteRepository(db);

        (await repository.GetVersionAsync()).Should().Be(0);

        var first = await repository.ReplaceAllAsync([Model("a"), Model("b")]);
        first.Should().Be(1);

        var snapshot = await repository.ListWithVersionAsync();
        snapshot.Version.Should().Be(1);
        snapshot.Models.Select(m => m.Id).Should().BeEquivalentTo(["a", "b"]);

        (await repository.ReplaceAllAsync([Model("a")], expectedVersion: 1)).Should().Be(2);
        (await repository.ListAsync()).Select(m => m.Id).Should().BeEquivalentTo(["a"]);
    }

    /// <summary>
    /// State is durable, not a runtime flag: a route stopped before a restart must still be stopped
    /// after one, or restarting the gateway would silently put every stopped model back in service.
    /// </summary>
    [Fact]
    public async Task ReplaceAll_RoundTripsRouteState()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var stopped = Model("stopped");
            stopped.State = ModelRouteStates.Stopped;
            await new ModelRouteRepository(db).ReplaceAllAsync([Model("serving"), stopped]);
        }

        // A fresh context, as a restarted process would use.
        await using var reread = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var routes = await new ModelRouteRepository(reread).ListAsync();

        routes.Single(m => m.Id == "stopped").IsStopped().Should().BeTrue();
        routes.Single(m => m.Id == "serving").IsServing().Should().BeTrue();
    }

    [Fact]
    public async Task ReplaceAll_WithStaleExpectedVersion_ThrowsAndKeepsTheOtherWritersRoutes()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new ModelRouteRepository(db);

        var read = await repository.ListWithVersionAsync();

        // Another admin (or another replica) writes first.
        await using (var otherDb = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await new ModelRouteRepository(otherDb).ReplaceAllAsync([Model("theirs")]);
        }

        var act = async () => await repository.ReplaceAllAsync([Model("mine")], expectedVersion: read.Version);

        await act.Should().ThrowAsync<ModelRouteVersionConflictException>();

        await using var verifyDb = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        (await new ModelRouteRepository(verifyDb).ListAsync())
            .Select(m => m.Id)
            .Should().BeEquivalentTo(["theirs"], "a stale write must not delete the routes it never saw");
    }

    /// <summary>
    /// Deleting one route rewrites every remaining row under the unique model_id index; this is the
    /// delete-then-recreate cycle an operator drives from the admin UI.
    /// </summary>
    [Fact]
    public async Task ReplaceAll_RemoveThenReAddSameId_Succeeds()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new ModelRouteRepository(db);

        var version = await repository.ReplaceAllAsync([Model("a"), Model("b"), Model("c")]);
        version = await repository.ReplaceAllAsync([Model("a"), Model("b")], version);
        version = await repository.ReplaceAllAsync([Model("a"), Model("b"), Model("c")], version);

        (await repository.ListAsync()).Select(m => m.Id).Should().BeEquivalentTo(["a", "b", "c"]);
        version.Should().Be(3);
    }

    /// <summary>
    /// The version check only protects routes if the read and the rewrite cannot interleave. With a
    /// deferred transaction two admins both passed the check and the loser died with
    /// SQLITE_BUSY_SNAPSHOT (a 500), not the documented conflict. Under BEGIN IMMEDIATE the second
    /// writer waits for the first and then sees the bumped version.
    /// </summary>
    [Fact]
    public async Task ReplaceAll_ConcurrentWriters_SurfaceOnlyVersionConflicts()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        const int writers = 8;
        var conflicts = 0;
        var successes = 0;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(async i =>
        {
            await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
            var repository = new ModelRouteRepository(db);
            var seen = await repository.GetVersionAsync();
            try
            {
                await repository.ReplaceAllAsync([Model($"m-{i}")], expectedVersion: seen);
                Interlocked.Increment(ref successes);
            }
            catch (ModelRouteVersionConflictException)
            {
                Interlocked.Increment(ref conflicts);
            }
        }));

        (successes + conflicts).Should().Be(writers, "no writer may fail with anything but a version conflict");
        successes.Should().BeGreaterThan(0);

        await using var verify = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        (await new ModelRouteRepository(verify).GetVersionAsync()).Should().Be(successes);
    }

    [Fact]
    public async Task ReplaceAll_TakesTheWriteLockUpFront()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        var interceptor = new TransactionRecordingInterceptor();
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new GatewayDbContext(options);
        await new ModelRouteRepository(db).ReplaceAllAsync([Model("a")]);

        interceptor.StartedIsolationLevels
            .Should().ContainSingle()
            .Which.Should().Be(System.Data.IsolationLevel.Serializable);
    }

    /// <summary>
    /// The registry resolves ids case-insensitively, so two routes that differ only in case would
    /// leave "which one wins" to insertion order. The NOCASE unique index rejects the pair.
    /// </summary>
    [Fact]
    public async Task ReplaceAll_WithIdsDifferingOnlyInCase_IsRejectedByTheIndex()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new ModelRouteRepository(db);

        var act = () => repository.ReplaceAllAsync([Model("GPT-4o"), Model("gpt-4o")]);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task ReplaceAll_WithEmptySet_ClearsTheTable()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);

        await using var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        var repository = new ModelRouteRepository(db);

        var version = await repository.ReplaceAllAsync([Model("only")]);
        await repository.ReplaceAllAsync([], version);

        (await repository.ListAsync()).Should().BeEmpty();
    }

    /// <summary>Records the isolation level of every transaction EF starts or is handed on the context.</summary>
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
