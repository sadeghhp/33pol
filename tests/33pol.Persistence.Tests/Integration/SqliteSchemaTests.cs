using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// Exercises the generated SQLite migrations against a real SQLite engine (the EF InMemory
/// provider used by the other repository tests never runs migration DDL). A shared-cache
/// in-memory database is kept alive by an open connection so the schema created by MigrateAsync
/// survives across the repository's own connections.
/// </summary>
public sealed class SqliteSchemaTests
{
    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    [Fact]
    public async Task Migrations_Apply_AndTenantRepository_RoundTrips()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var sut = new TenantRepository(db);
            var now = DateTimeOffset.UtcNow;
            await sut.CreateAsync(new TenantRecord(
                Guid.NewGuid(), "acme", "Acme Corp", "standard", "cc-1", true, now, now));

            var loaded = await sut.GetBySlugAsync("acme");

            loaded.Should().NotBeNull();
            loaded!.Slug.Should().Be("acme");
            loaded.PlanSlug.Should().Be("standard");
        }
    }

    [Fact]
    public async Task ForeignKeys_AreEnforced_CascadeDeletesApiKeys()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var tenantId = Guid.NewGuid();

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();

            var now = DateTimeOffset.UtcNow;
            db.Tenants.Add(new TenantEntity
            {
                Id = tenantId,
                Slug = "acme",
                Name = "Acme Corp",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.ApiKeys.Add(new ApiKeyEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                KeyHash = "hash",
                KeyPrefix = "sk-prefix",
                Role = ApiKeyRole.Admin,
                Scopes = ["admin"],
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var tenant = await db.Tenants.FindAsync(tenantId);
            tenant.Should().NotBeNull();
            db.Tenants.Remove(tenant!);
            await db.SaveChangesAsync();

            // foreign_keys=ON means the tenant's api keys must be gone; if the pragma were off
            // they would be orphaned and this count would be 1.
            var remaining = await db.ApiKeys.CountAsync(k => k.TenantId == tenantId);
            remaining.Should().Be(0);
        }
    }
}
