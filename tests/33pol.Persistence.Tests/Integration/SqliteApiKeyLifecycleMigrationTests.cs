using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pol33.Core.Identity;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// <c>ApiKeyArchiveAndLifecycle</c> adds the archive column and the history table. The history table
/// is what makes permanent deletion auditable, so it must not start out blank for the keys that
/// already exist — those are exactly the credentials an audit asks about first.
/// </summary>
public sealed class SqliteApiKeyLifecycleMigrationTests
{
    /// <summary>The last migration before the lifecycle table.</summary>
    private const string BeforeLifecycle = "20260826163906_ScopedRateLimitRules";

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static readonly DateTimeOffset RevokedAt = new(2026, 6, 7, 8, 9, 10, TimeSpan.Zero);

    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    /// <summary>
    /// EF Core's SQLite provider binds Guid parameters as uppercase dashed text, and SQLite compares
    /// TEXT case-sensitively. Hand-written rows must use the same casing or nothing will ever find them.
    /// </summary>
    private static string Sql(Guid id) => id.ToString("D").ToUpperInvariant();

    [Fact]
    public async Task Migration_BackfillsHistoryForExistingKeys()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeLifecycle);
        }

        var liveKeyId = Guid.NewGuid();
        var revokedKeyId = Guid.NewGuid();

        await using (var command = keepAlive.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO tenants (Id, Slug, Name, PlanSlug, IsActive, CreatedAt, UpdatedAt) VALUES " +
                $"('{Sql(TenantId)}', 'acme', 'Acme', 'free', 1, {CreatedAt.UtcTicks}, {CreatedAt.UtcTicks}); " +
                "INSERT INTO api_keys (Id, TenantId, KeyHash, KeyPrefix, Role, Scopes, CreatedAt, RevokedAt, LastUsedAt, Label) VALUES " +
                $"('{Sql(liveKeyId)}', '{Sql(TenantId)}', 'hash-live', 'sk-live', 'Inference', '[]', {CreatedAt.UtcTicks}, NULL, NULL, 'live one'), " +
                $"('{Sql(revokedKeyId)}', '{Sql(TenantId)}', 'hash-gone', 'sk-gone', 'Admin', '[]', {CreatedAt.UtcTicks}, {RevokedAt.UtcTicks}, {RevokedAt.UtcTicks}, 'revoked one');";
            await command.ExecuteNonQueryAsync();
        }

        await using var upgraded = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        await upgraded.Database.MigrateAsync();

        var lifecycle = new ApiKeyLifecycleEventRepository(upgraded);

        // The never-revoked key gets its creation recorded and nothing else.
        var liveHistory = await lifecycle.ListForKeyAsync(TenantId, liveKeyId);
        liveHistory.Select(e => e.Event).Should().Equal([ApiKeyLifecycleEvent.Created]);
        liveHistory[0].KeyPrefix.Should().Be("sk-live");
        liveHistory[0].Label.Should().Be("live one");
        liveHistory[0].OccurredAt.Should().Be(CreatedAt);
        liveHistory[0].HadUsage.Should().BeFalse();
        liveHistory[0].ActorApiKeyId.Should().BeNull("nothing recorded who acted before this table existed");

        // The already-revoked key gets both events, in order.
        var revokedHistory = await lifecycle.ListForKeyAsync(TenantId, revokedKeyId);
        revokedHistory.Select(e => e.Event).Should().Equal(
            [ApiKeyLifecycleEvent.Created, ApiKeyLifecycleEvent.Revoked]);
        revokedHistory[1].OccurredAt.Should().Be(RevokedAt);
        revokedHistory[1].HadUsage.Should().BeTrue("the key had a LastUsedAt when it was revoked");

        // Backfilled ids are real, distinct Guids in the casing every other row uses — a lowercase
        // id would insert and read back fine, then match nothing on a lookup.
        var ids = liveHistory.Concat(revokedHistory).Select(e => e.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().NotContain(Guid.Empty);
    }

    [Fact]
    public async Task Migration_LeavesExistingKeysUnarchived()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeLifecycle);
        }

        var keyId = Guid.NewGuid();

        await using (var command = keepAlive.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO tenants (Id, Slug, Name, PlanSlug, IsActive, CreatedAt, UpdatedAt) VALUES " +
                $"('{Sql(TenantId)}', 'acme', 'Acme', 'free', 1, {CreatedAt.UtcTicks}, {CreatedAt.UtcTicks}); " +
                "INSERT INTO api_keys (Id, TenantId, KeyHash, KeyPrefix, Role, Scopes, CreatedAt) VALUES " +
                $"('{Sql(keyId)}', '{Sql(TenantId)}', 'hash', 'sk-a', 'Inference', '[]', {CreatedAt.UtcTicks});";
            await command.ExecuteNonQueryAsync();
        }

        await using var upgraded = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        await upgraded.Database.MigrateAsync();

        // An upgrade must not file anything away: every existing key stays in the working set.
        var keys = await new ApiKeyRepository(upgraded).ListByTenantAsync(TenantId);
        keys.Should().ContainSingle().Which.ArchivedAt.Should().BeNull();
    }
}
