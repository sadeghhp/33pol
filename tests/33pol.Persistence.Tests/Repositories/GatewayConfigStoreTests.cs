using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class GatewayConfigStoreTests
{
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
