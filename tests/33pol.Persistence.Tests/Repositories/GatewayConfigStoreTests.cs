using Pol33.Core.RateLimiting;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class GatewayConfigStoreTests
{
    [Fact]
    public async Task RateLimits_SaveThenLoad_RoundTripsAndBumpsVersion()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(RateLimits_SaveThenLoad_RoundTripsAndBumpsVersion));

        await new RateLimitSettingsRepository(db).SaveAsync(
            new RateLimitPolicy(55, 5, 4),
            new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro"] = new(200, 20, 15),
            });

        var snapshot = await new GatewayConfigStore(db).LoadSnapshotAsync();

        snapshot.RateLimits.Default.Rpm.Should().Be(55);
        snapshot.RateLimits.Default.MaxConcurrentStreams.Should().Be(4);
        snapshot.RateLimits.Plans["PRO"].Rpm.Should().Be(200); // OrdinalIgnoreCase lookup
        snapshot.Version.Should().Be(1); // save bumped the config version
    }

    [Fact]
    public async Task Version_StartsAtZero_ThenIncrementsMonotonically()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(Version_StartsAtZero_ThenIncrementsMonotonically));
        var sut = new GatewayConfigStore(db);

        (await sut.GetVersionAsync()).Should().Be(0);

        (await sut.IncrementVersionAsync()).Should().Be(1);
        (await sut.IncrementVersionAsync()).Should().Be(2);

        (await sut.GetVersionAsync()).Should().Be(2);

        var snapshot = await sut.LoadSnapshotAsync();
        snapshot.Version.Should().Be(2);
    }
}
