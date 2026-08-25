using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pol33.Core.Models;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// <c>ModelRouteState</c> adds <c>model_routes.State</c>. Every route that already exists was, by
/// definition, serving — so the upgrade must backfill them rather than leave a column the registry
/// reads as something else and takes the whole gateway's traffic offline on the first restart.
/// </summary>
public sealed class SqliteModelRouteStateMigrationTests
{
    /// <summary>The last migration before the state column.</summary>
    private const string BeforeState = "20260825073757_AddClientDisconnects";

    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    [Fact]
    public async Task Migration_BackfillsExistingRoutesAsServing()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeState);
        }

        await using (var command = keepAlive.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO model_routes (Id, ModelId, Url, MaxContextLength, Aliases, Capabilities, PublicAccess, UpdatedAt) VALUES " +
                $"('{Guid.NewGuid():D}', 'legacy-a', 'http://a', 8192, '[]', '[]', 0, 1), " +
                $"('{Guid.NewGuid():D}', 'legacy-b', 'http://b', 4096, '[\"b-alias\"]', '[]', 1, 2);";
            await command.ExecuteNonQueryAsync();
        }

        await using var upgraded = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        await upgraded.Database.MigrateAsync();

        var routes = await new ModelRouteRepository(upgraded).ListAsync();

        routes.Select(m => m.Id).Should().BeEquivalentTo(["legacy-a", "legacy-b"]);
        routes.Should().OnlyContain(m => m.State == ModelRouteStates.Serving);
        routes.Should().OnlyContain(m => m.IsServing());

        // The rest of each route came through untouched.
        var b = routes.Single(m => m.Id == "legacy-b");
        b.Url.Should().Be("http://b");
        b.MaxContextLength.Should().Be(4096);
        b.Aliases.Should().BeEquivalentTo(["b-alias"]);
        b.PublicAccess.Should().BeTrue();
    }
}
