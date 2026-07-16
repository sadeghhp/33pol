using Pol33.Core.Models;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class GatewayStatsSnapshotStoreTests
{
    private static RecentRequestEntry Entry(string id, DateTimeOffset timestamp) => new()
    {
        RequestId = id,
        Method = "POST",
        Path = "/v1/chat/completions",
        ModelId = "m1",
        TenantId = "t1",
        StatusCode = 200,
        DurationMs = 12.5,
        IsStreaming = true,
        ErrorCode = null,
        TimestampUtc = timestamp,
    };

    [Fact]
    public async Task LoadAsync_WhenNoSnapshot_ReturnsNull()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(LoadAsync_WhenNoSnapshot_ReturnsNull));
        var store = new GatewayStatsSnapshotStore(db);

        (await store.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsCountersDictionariesAndRecentFeed()
    {
        var dbName = nameof(SaveThenLoad_RoundTripsCountersDictionariesAndRecentFeed);
        var t0 = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var snapshot = new GatewayRuntimeSnapshot
        {
            TotalRequests = 42,
            TotalErrors = 5,
            TotalLatencyMs = 8400,
            RateLimitRejections = 2,
            QuotaRejections = 1,
            RequestsPerModel = new Dictionary<string, long> { ["m1"] = 30, ["m2"] = 12 },
            ErrorsPerModel = new Dictionary<string, long> { ["m1"] = 5 },
            Recent = [Entry("r1", t0), Entry("r2", t0.AddSeconds(1))],
        };

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            await new GatewayStatsSnapshotStore(db).SaveAsync(snapshot);
        }

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            var loaded = await new GatewayStatsSnapshotStore(db).LoadAsync();

            loaded.Should().NotBeNull();
            loaded!.TotalRequests.Should().Be(42);
            loaded.TotalErrors.Should().Be(5);
            loaded.TotalLatencyMs.Should().Be(8400);
            loaded.RateLimitRejections.Should().Be(2);
            loaded.QuotaRejections.Should().Be(1);
            loaded.RequestsPerModel["m1"].Should().Be(30);
            loaded.RequestsPerModel["m2"].Should().Be(12);
            loaded.ErrorsPerModel["m1"].Should().Be(5);
            loaded.Recent.Select(e => e.RequestId).Should().ContainInOrder("r1", "r2");
            loaded.Recent[0].IsStreaming.Should().BeTrue();
        }
    }

    [Fact]
    public async Task SaveAsync_ReplacesPreviousSnapshotAndRecentFeed()
    {
        var dbName = nameof(SaveAsync_ReplacesPreviousSnapshotAndRecentFeed);
        var t0 = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            await new GatewayStatsSnapshotStore(db).SaveAsync(new GatewayRuntimeSnapshot
            {
                TotalRequests = 10,
                Recent = [Entry("old-1", t0), Entry("old-2", t0.AddSeconds(1))],
            });
        }

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            await new GatewayStatsSnapshotStore(db).SaveAsync(new GatewayRuntimeSnapshot
            {
                TotalRequests = 20,
                Recent = [Entry("new-1", t0.AddSeconds(2))],
            });
        }

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            var loaded = await new GatewayStatsSnapshotStore(db).LoadAsync();

            loaded!.TotalRequests.Should().Be(20); // single row updated, not duplicated
            loaded.Recent.Should().ContainSingle(e => e.RequestId == "new-1");
            loaded.Recent.Should().NotContain(e => e.RequestId == "old-1");
        }
    }
}
