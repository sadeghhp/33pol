using Pol33.Core.Models;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class QuotaUsageSnapshotStoreTests
{
    [Fact]
    public async Task LoadAsync_WhenEmpty_ReturnsEmpty()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(LoadAsync_WhenEmpty_ReturnsEmpty));

        (await new QuotaUsageSnapshotStore(db).LoadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsUsage()
    {
        var dbName = nameof(SaveThenLoad_RoundTripsUsage);

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            await new QuotaUsageSnapshotStore(db).SaveAsync([
                new QuotaUsageSnapshot("key-a", "2026-07", 300),
                new QuotaUsageSnapshot("key-b", "2026-07", 50),
            ]);
        }

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            var loaded = await new QuotaUsageSnapshotStore(db).LoadAsync();

            loaded.Should().HaveCount(2);
            loaded.Should().ContainSingle(u => u.PartitionKey == "key-a" && u.Used == 300 && u.Period == "2026-07");
            loaded.Should().ContainSingle(u => u.PartitionKey == "key-b" && u.Used == 50);
        }
    }

    [Fact]
    public async Task SaveAsync_UpsertsByPartitionKey()
    {
        var dbName = nameof(SaveAsync_UpsertsByPartitionKey);

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            await new QuotaUsageSnapshotStore(db).SaveAsync([new QuotaUsageSnapshot("key-a", "2026-07", 100)]);
        }

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            await new QuotaUsageSnapshotStore(db).SaveAsync([new QuotaUsageSnapshot("key-a", "2026-07", 250)]);
        }

        await using (var db = PersistenceTestDbContextFactory.CreateInMemory(dbName))
        {
            var loaded = await new QuotaUsageSnapshotStore(db).LoadAsync();

            loaded.Should().ContainSingle();
            loaded[0].Used.Should().Be(250);
        }
    }
}
