using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pol33.Persistence.Migrations;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// <c>20260719100201_DateTimeOffsetAsUtcTicks</c> switched timestamp columns from TEXT to INTEGER via a
/// table rebuild that copies values verbatim, so a database created before it kept ISO-8601 text in
/// INTEGER-affinity columns and read back year-0001 timestamps. <c>DateTimeOffsetTextToUtcTicks</c>
/// repairs those rows in place; these tests drive a database through the pre-tick schema with text
/// values and check the repaired values, NULL handling, and idempotency.
/// </summary>
public sealed class SqliteDateTimeOffsetTextRepairMigrationTests
{
    /// <summary>The last migration before timestamps became ticks.</summary>
    private const string PreTickMigration = "20260719070507_QuotaSettings";

    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    /// <summary>Microsoft.Data.Sqlite stores Guids as upper-case text and compares parameters as such.</summary>
    private static string Upper(Guid id) => id.ToString("D").ToUpperInvariant();

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Migration_ConvertsLegacyTextTimestampsToUtcTicks_AndIsIdempotent()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 7, 19, 5, 31, 30, TimeSpan.Zero);
        // A non-UTC offset with fractional seconds: the conversion must land on the true instant.
        var expires = new DateTimeOffset(2026, 12, 31, 23, 59, 58, 250, TimeSpan.FromHours(2));

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(PreTickMigration);
        }

        // Rows exactly as the pre-tick provider wrote them: DateTimeOffset as ISO-8601 text.
        await ExecuteAsync(keepAlive,
            $"INSERT INTO tenants (Id, Slug, Name, IsActive, CreatedAt, UpdatedAt) VALUES " +
            $"('{Upper(tenantId)}', 'acme', 'Acme', 1, '2026-07-19 05:31:30+00:00', '2026-07-19 05:31:30+00:00');");
        await ExecuteAsync(keepAlive,
            $"INSERT INTO api_keys (Id, TenantId, KeyHash, KeyPrefix, Role, Scopes, ExpiresAt, RevokedAt, CreatedAt, LastUsedAt) VALUES " +
            $"('{Upper(keyId)}', '{Upper(tenantId)}', 'hash', 'pfx', 'Inference', '[]', '2026-12-31 23:59:58.25+02:00', NULL, '2026-07-19 05:31:30+00:00', NULL);");

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();

            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId);
            tenant.CreatedAt.Should().Be(created);
            tenant.UpdatedAt.Should().Be(created);

            var key = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == keyId);
            key.CreatedAt.Should().Be(created);
            key.ExpiresAt.Should().Be(expires, "the +02:00 offset and the fractional second are honoured");
            key.RevokedAt.Should().BeNull();
            key.LastUsedAt.Should().BeNull();
        }

        (await ScalarAsync(keepAlive, "SELECT typeof(CreatedAt) FROM tenants")).Should().Be("integer");
        (await ScalarAsync(keepAlive, "SELECT typeof(ExpiresAt) FROM api_keys")).Should().Be("integer");

        // Re-running the repair is a no-op on already-numeric rows.
        var before = (long)(await ScalarAsync(keepAlive, "SELECT CreatedAt FROM tenants"))!;
        await ExecuteAsync(keepAlive,
            $"UPDATE tenants SET CreatedAt = {DateTimeOffsetTextToUtcTicks.ToUtcTicksSql("CreatedAt")} WHERE typeof(CreatedAt) = 'text';");
        (await ScalarAsync(keepAlive, "SELECT CreatedAt FROM tenants")).Should().Be(before);
        before.Should().Be(created.UtcTicks);
    }

    /// <summary>
    /// The conversion expression itself, against the values the old provider produced.
    /// </summary>
    [Theory]
    [InlineData("2026-07-19 05:31:30+00:00", 2026, 7, 19, 5, 31, 30, 0, 0)]
    [InlineData("2026-07-19 05:31:30.1234567+00:00", 2026, 7, 19, 5, 31, 30, 123, 0)]
    [InlineData("2026-01-01 00:00:00-05:00", 2026, 1, 1, 5, 0, 0, 0, 0)]
    [InlineData("1970-01-01 00:00:00+00:00", 1970, 1, 1, 0, 0, 0, 0, 0)]
    public async Task ToUtcTicksSql_MatchesDotNetUtcTicks(
        string text, int y, int mo, int d, int h, int mi, int s, int ms, int offsetHours)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "CREATE TABLE t (c INTEGER);");
        await ExecuteAsync(connection, $"INSERT INTO t (c) VALUES ('{text}');");
        (await ScalarAsync(connection, "SELECT typeof(c) FROM t")).Should().Be("text", "INTEGER affinity keeps non-numeric text as text");

        await ExecuteAsync(connection, $"UPDATE t SET c = {DateTimeOffsetTextToUtcTicks.ToUtcTicksSql("c")} WHERE typeof(c) = 'text';");

        var expected = new DateTimeOffset(y, mo, d, h, mi, s, ms, TimeSpan.FromHours(offsetHours));
        (await ScalarAsync(connection, "SELECT c FROM t")).Should().Be(expected.UtcTicks);
    }
}
