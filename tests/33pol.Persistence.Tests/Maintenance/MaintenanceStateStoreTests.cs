using Pol33.Core.Abstractions;
using Pol33.Core.Models.Overview;
using Pol33.Persistence.Maintenance;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Maintenance;

public sealed class MaintenanceStateStoreTests
{
    [Fact]
    public async Task Get_MissingKey_ReturnsNull()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(Get_MissingKey_ReturnsNull));
        var store = new MaintenanceStateStore(db);

        (await store.GetAsync<BackupStatus>(MaintenanceStateKeys.LastBackup)).Should().BeNull();
    }

    [Fact]
    public async Task Set_ThenGet_RoundTripsAndOverwrites()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(nameof(Set_ThenGet_RoundTripsAndOverwrites));
        var store = new MaintenanceStateStore(db);
        var first = new BackupStatus { AttemptedAtUtc = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero), Succeeded = false, Error = "boom", IntegrityCheck = "skipped" };
        var second = first with { AttemptedAtUtc = first.AttemptedAtUtc.AddHours(1), Succeeded = true, Error = null, Path = "/tmp/b.db", SizeBytes = 42, IntegrityCheck = "ok" };

        await store.SetAsync(MaintenanceStateKeys.LastBackup, first);
        await store.SetAsync(MaintenanceStateKeys.LastBackup, second);

        var stored = await store.GetAsync<BackupStatus>(MaintenanceStateKeys.LastBackup);
        stored.Should().BeEquivalentTo(second);
        db.MaintenanceState.Count().Should().Be(1);
    }
}
