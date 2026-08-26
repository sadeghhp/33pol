using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Repositories;
using Pol33.Persistence.Tests.Infrastructure;

namespace Pol33.Persistence.Tests.Repositories;

public sealed class GatewayConfigStoreTests
{
    [Fact]
    public async Task Quota_RowPresent_LoadsScalarsWithDecimalToDoubleConversion()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(Quota_RowPresent_LoadsScalarsWithDecimalToDoubleConversion));

        db.QuotaSettings.Add(new QuotaSettingsEntity
        {
            Id = 1,
            DefaultMonthlyTokenLimit = 2_500_000,
            SoftLimitRatio = 0.75m,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var snapshot = await new GatewayConfigStore(db).LoadSnapshotAsync();

        snapshot.Quota.DefaultMonthlyTokenLimit.Should().Be(2_500_000);
        snapshot.Quota.SoftLimitRatio.Should().Be(0.75);
    }

    [Fact]
    public async Task Quota_NoRow_FallsBackToDefaults()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(Quota_NoRow_FallsBackToDefaults));

        var snapshot = await new GatewayConfigStore(db).LoadSnapshotAsync();

        snapshot.Quota.Should().BeSameAs(QuotaConfigSection.Defaults);
    }

    [Fact]
    public async Task RateLimits_SaveThenLoad_RoundTripsAndBumpsVersion()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(RateLimits_SaveThenLoad_RoundTripsAndBumpsVersion));

        await new RateLimitSettingsRepository(db).SaveAsync(
            enabled: true,
            adaptiveEnabled: false,
            new RateLimitPolicy(55, 5, 4),
            new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["pro"] = new(200, 20, 15),
            },
            []);

        var snapshot = await new GatewayConfigStore(db).LoadSnapshotAsync();

        snapshot.RateLimits.Enabled.Should().BeTrue();
        snapshot.RateLimits.Default.Rpm.Should().Be(55);
        snapshot.RateLimits.Default.MaxConcurrentStreams.Should().Be(4);
        snapshot.RateLimits.Plans["PRO"].Rpm.Should().Be(200); // OrdinalIgnoreCase lookup
        snapshot.Version.Should().Be(1); // save bumped the config version
    }

    [Fact]
    public async Task RateLimits_SaveDisabled_RoundTripsFlagAndKeepsTierValues()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(RateLimits_SaveDisabled_RoundTripsFlagAndKeepsTierValues));

        await new RateLimitSettingsRepository(db).SaveAsync(
            enabled: false,
            adaptiveEnabled: false,
            new RateLimitPolicy(55, 5, 4),
            new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase),
            []);

        var snapshot = await new GatewayConfigStore(db).LoadSnapshotAsync();

        snapshot.RateLimits.Enabled.Should().BeFalse();
        // Tier values survive a disable so re-enabling restores the configured limits.
        snapshot.RateLimits.Default.Rpm.Should().Be(55);
        snapshot.RateLimits.Default.MaxConcurrentStreams.Should().Be(4);
    }

    [Fact]
    public async Task RateLimits_WithNoDefaultsRow_DefaultsToEnabled()
    {
        await using var db = PersistenceTestDbContextFactory.CreateInMemory(
            nameof(RateLimits_WithNoDefaultsRow_DefaultsToEnabled));

        var snapshot = await new GatewayConfigStore(db).LoadSnapshotAsync();

        // A database-less / pre-seed gateway must enforce, never silently allow everything.
        snapshot.RateLimits.Enabled.Should().BeTrue();
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
