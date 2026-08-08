using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Models;
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
}
