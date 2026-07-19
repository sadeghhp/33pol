using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// Fail-static contract: the config syncer (GatewayConfigSnapshotService) keeps the last-good snapshot
/// only because a corrupt/unreadable database surfaces as a THROWN exception from LoadSnapshotAsync — it
/// must never silently return a degraded snapshot, which the syncer would swap in as if it were good.
/// A live drill exercises the running gateway; this pins the store-level contract in CI against real SQLite.
/// </summary>
public sealed class GatewayConfigStoreFailStaticTests
{
    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    [Fact]
    public async Task LoadSnapshotAsync_WithMalformedCorsRow_Throws_NotSilentlyDegrades()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();
            db.CorsSettings.Add(new CorsSettingsEntity
            {
                Id = 1,
                AllowedOrigins = ["https://good.example.com"],
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // Corrupt the JSON TEXT column out-of-band, mimicking a bad row / partial write on disk.
            // Passed as a parameter so EF does not treat the literal braces as a {0} placeholder.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE cors_settings SET AllowedOrigins = {0} WHERE Id = 1", "{ not valid json");
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var store = new GatewayConfigStore(db);

            var act = async () => await store.LoadSnapshotAsync();

            await act.Should().ThrowAsync<Exception>(
                "a corrupt config row must surface as an exception so the syncer keeps the last-good snapshot");
        }
    }

    [Fact]
    public async Task LoadSnapshotAsync_WithValidRows_RoundTrips()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();
            db.CorsSettings.Add(new CorsSettingsEntity
            {
                Id = 1,
                AllowedOrigins = ["https://good.example.com"],
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var store = new GatewayConfigStore(db);

            var snapshot = await store.LoadSnapshotAsync();

            snapshot.Cors.AllowedOrigins.Should().ContainSingle().Which.Should().Be("https://good.example.com");
        }
    }
}
