using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Core.Billing;
using Pol33.Core.Identity;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Integration;

/// <summary>
/// Regression guard for the SQLite "ORDER BY DateTimeOffset" limitation. Before DateTimeOffset was
/// persisted as UTC ticks, these queries threw NotSupportedException at runtime against real SQLite —
/// but passed on the InMemory provider the other repository tests use, so the whole suite was blind to it
/// and the admin key-list / billing-event endpoints 500'd in Production. These must run on real SQLite.
/// </summary>
public sealed class SqliteDateTimeOffsetOrderingTests
{
    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    [Fact]
    public async Task ApiKeyRepository_ListByTenant_OrdersByCreatedAtDescending()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var tenantId = Guid.NewGuid();
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();
            db.Tenants.Add(new TenantEntity
            {
                Id = tenantId,
                Slug = "acme",
                Name = "Acme Corp",
                IsActive = true,
                CreatedAt = baseTime,
                UpdatedAt = baseTime,
            });
            // Insert out of chronological order so a correct ORDER BY is observable.
            foreach (var offsetDays in new[] { 5, 1, 3 })
            {
                db.ApiKeys.Add(new ApiKeyEntity
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    KeyHash = $"hash-{offsetDays}",
                    KeyPrefix = $"sk-{offsetDays}",
                    Role = ApiKeyRole.Inference,
                    Scopes = ["inference"],
                    CreatedAt = baseTime.AddDays(offsetDays),
                });
            }
            await db.SaveChangesAsync();
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var sut = new ApiKeyRepository(db);

            // The bug manifested here as a thrown NotSupportedException, not a wrong result.
            var keys = await sut.ListByTenantAsync(tenantId);

            keys.Select(k => k.CreatedAt).Should().BeInDescendingOrder();
            keys.Select(k => k.KeyPrefix).Should().ContainInOrder("sk-5", "sk-3", "sk-1");
        }
    }

    [Fact]
    public async Task BillingEventRepository_Query_OrdersByRecordedAtDescending_WithRangeFilter()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        var tenantId = Guid.NewGuid();
        var baseTime = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            await db.Database.MigrateAsync();
            foreach (var offsetHours in new[] { 10, 2, 6 })
            {
                db.BillingEvents.Add(new BillingEventEntity
                {
                    Id = Guid.NewGuid(),
                    RequestId = $"req-{offsetHours}",
                    TenantId = tenantId,
                    RecordedAt = baseTime.AddHours(offsetHours),
                    ModelId = "gpt-x",
                    PromptTokens = 1,
                    CompletionTokens = 1,
                    TotalCost = 0.01m,
                    DurationMs = 5,
                });
            }
            await db.SaveChangesAsync();
        }

        await using (var db = PersistenceTestDbContextFactory.CreateSqlite(connectionString))
        {
            var sut = new BillingEventRepository(db);

            // FromDate/ToDate exercise the RecordedAt range filter; OrderByDescending + Take was the
            // ORDER BY DateTimeOffset site that threw against real SQLite.
            var results = await sut.QueryAsync(new BillingEventQuery(
                FromDate: new DateOnly(2026, 6, 1),
                ToDate: new DateOnly(2026, 6, 2),
                TenantId: tenantId,
                Limit: 10));

            results.Select(e => e.RecordedAt).Should().BeInDescendingOrder();
            results.Should().HaveCount(3);
        }
    }
}
