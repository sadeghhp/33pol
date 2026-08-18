using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pol33.Core.Billing;
using Pol33.Persistence;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// A concurrent writer (second gateway instance, reconciliation rerun) can land a request id between
/// the repository's existence probe and its commit; the unique index then rejects the INSERT and the
/// append correctly reports "duplicate". What must <em>not</em> happen is the failed row staying
/// tracked as Added: every later SaveChanges on the same scoped context — rollups, last-used touches,
/// the rest of a batch — would retry that INSERT and fail again, so a whole batch of rollups was lost.
/// The race is reproduced deterministically by inserting the twin row from another connection at the
/// moment the probe runs.
/// </summary>
public sealed class SqliteBillingEventDuplicateRaceTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    private static BillingEventRecord Event(string requestId, Guid tenant) =>
        new(Guid.NewGuid(), requestId, tenant, null, "gpt-4o", null, 10, 5, null, null, 0.01m, 10, At);

    [Fact]
    public async Task TryAppendAsync_AfterLosingTheRace_LeavesTheContextUsable()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        await using (var setup = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await setup.Database.MigrateAsync();
        }

        var tenant = Guid.NewGuid();
        var racer = new RaceInterceptor(connectionString, Event("req_race", tenant));
        var options = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(racer)
            .Options;

        await using var db = new GatewayDbContext(options);
        var sut = new BillingEventRepository(db);

        (await sut.TryAppendAsync(Event("req_race", tenant))).Should().BeFalse("the other writer won the race");
        racer.Fired.Should().BeTrue("the test only means something if the twin row was inserted mid-flight");
        db.ChangeTracker.Entries().Should().BeEmpty("the failed row must not stay tracked as Added");

        // The same scoped context keeps working: another append and a rollup increment both save.
        (await sut.TryAppendAsync(Event("req_next", tenant))).Should().BeTrue();
        var usageDate = DateOnly.FromDateTime(At.UtcDateTime);
        var rollups = new DailyUsageRollupRepository(db);
        await rollups.IncrementRollupsAsync([new DailyUsageRollupDelta(usageDate, tenant, "gpt-4o", null, 10, 5, 0.01m, 1)]);

        (await rollups.GetRollupsAsync(usageDate, usageDate, tenant)).Should().ContainSingle();
        (await db.BillingEvents.AsNoTracking().CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// Inserts the twin row from a second connection the first time the existence probe for it
    /// runs, so the probe misses it and the INSERT hits the unique index.
    /// </summary>
    private sealed class RaceInterceptor(string connectionString, BillingEventRecord twin) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired
                && command.CommandText.Contains("billing_events", StringComparison.Ordinal)
                && command.CommandText.Contains("EXISTS", StringComparison.Ordinal))
            {
                Fired = true;
                await using var other = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
                (await new BillingEventRepository(other).TryAppendAsync(twin, cancellationToken)).Should().BeTrue();
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
