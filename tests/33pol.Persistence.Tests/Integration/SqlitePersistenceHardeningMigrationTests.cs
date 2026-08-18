using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pol33.Core.Billing;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// <c>PersistenceHardening</c> tightens <c>daily_usage_rollups</c> (sentinels instead of NULL keys)
/// and adds NOCASE to <c>model_routes.ModelId</c>. Databases that already carried the rows the old
/// schema allowed — duplicate NULL-keyed buckets, case-only duplicate routes — must come through with
/// their totals intact rather than failing the rebuild on the new constraints.
/// </summary>
public sealed class SqlitePersistenceHardeningMigrationTests
{
    /// <summary>The last migration before the hardening.</summary>
    private const string BeforeHardening = "20260817092856_RecentRequestUsage";

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
    public async Task Migration_MergesDuplicateNullKeyedRollups_AndKeepsTheirTotals()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeHardening);
        }

        var tenant = Guid.NewGuid();
        var usageDate = new DateOnly(2026, 6, 10);
        // Two anonymous / no-cost-centre rows for the same bucket (the old index let them in), plus
        // a tenant-scoped row that must be left alone.
        await ExecuteAsync(keepAlive,
            "INSERT INTO daily_usage_rollups (Id, UsageDate, TenantId, ModelId, CostCenter, PromptTokens, CompletionTokens, TotalCost, RequestCount, UpdatedAt) VALUES " +
            $"('{Guid.NewGuid():D}', '2026-06-10', NULL, 'gpt-4o', NULL, 10, 20, '0.5', 1, 1), " +
            $"('{Guid.NewGuid():D}', '2026-06-10', NULL, 'gpt-4o', NULL, 5, 5, '0.25', 2, 2), " +
            $"('{Guid.NewGuid():D}', '2026-06-10', '{Upper(tenant)}', 'gpt-4o', 'eng', 7, 7, '0.7', 7, 3);");

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();

            var repository = new DailyUsageRollupRepository(db);
            var all = await repository.GetScopedRollupsAsync(UsageScope.Unrestricted, usageDate, usageDate);
            all.Should().HaveCount(2);

            var anonymous = all.Single(r => r.TenantId is null);
            anonymous.CostCenter.Should().BeNull();
            anonymous.PromptTokens.Should().Be(15);
            anonymous.CompletionTokens.Should().Be(25);
            anonymous.RequestCount.Should().Be(3);
            anonymous.TotalCost.Should().Be(0.75m);

            var scoped = all.Single(r => r.TenantId == tenant);
            scoped.CostCenter.Should().Be("eng");
            scoped.RequestCount.Should().Be(7);
        }

        (await ScalarAsync(keepAlive, "SELECT COUNT(*) FROM daily_usage_rollups WHERE TenantId IS NULL OR CostCenter IS NULL")).Should().Be(0L);
    }

    [Fact]
    public async Task Migration_KeepsTheNewestOfCaseDuplicateRoutes()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync(BeforeHardening);
        }

        await ExecuteAsync(keepAlive,
            "INSERT INTO model_routes (Id, ModelId, Url, MaxContextLength, Aliases, Capabilities, PublicAccess, UpdatedAt) VALUES " +
            $"('{Guid.NewGuid():D}', 'GPT-4o', 'http://old', 8192, '[]', '[]', 0, 100), " +
            $"('{Guid.NewGuid():D}', 'gpt-4o', 'http://new', 8192, '[]', '[]', 0, 200), " +
            $"('{Guid.NewGuid():D}', 'other', 'http://other', 8192, '[]', '[]', 0, 50);");

        await using var migrated = PersistenceTestDbContextFactory.CreateSqlite(connectionString);
        await migrated.Database.MigrateAsync();

        var routes = await new ModelRouteRepository(migrated).ListAsync();
        routes.Select(r => r.Id).Should().BeEquivalentTo(["gpt-4o", "other"]);
        routes.Single(r => r.Id == "gpt-4o").Url.Should().Be("http://new", "the most recently updated route survives");
    }
}
