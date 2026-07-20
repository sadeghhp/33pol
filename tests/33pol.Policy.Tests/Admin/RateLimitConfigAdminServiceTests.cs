using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.Admin;

namespace Pol33.Policy.Tests.Admin;

public sealed class RateLimitConfigAdminServiceTests
{
    [Fact]
    public async Task UpdateAsync_ValidPayload_PersistsToRepositoryAndRefreshes()
    {
        var repo = new RecordingRepository();
        var refresher = new RecordingRefresher();
        var service = CreateService(new StubServiceProvider(repo, refresher));

        var result = await service.UpdateAsync(
            enabled: true,
            new RateLimitTierOptions { Rpm = 30, Burst = 3, MaxConcurrentStreams = 3 },
            new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["enterprise"] = new() { Rpm = 300, Burst = 30, MaxConcurrentStreams = 30 },
            });

        result.Success.Should().BeTrue();
        repo.SavedDefault!.Rpm.Should().Be(30);
        repo.SavedPlans!["enterprise"].Rpm.Should().Be(300);
        refresher.Refreshed.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_InvalidRpm_ReturnsValidationError()
    {
        var service = CreateService(new StubServiceProvider(null, null));

        var result = await service.UpdateAsync(
            enabled: true,
            new RateLimitTierOptions { Rpm = 0, Burst = 0, MaxConcurrentStreams = 0 },
            new Dictionary<string, RateLimitTierOptions>());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task UpdateAsync_NoDatabaseConfigured_Returns503()
    {
        var service = CreateService(new StubServiceProvider(null, null));

        var result = await service.UpdateAsync(
            enabled: true,
            new RateLimitTierOptions { Rpm = 60, Burst = 10, MaxConcurrentStreams = 5 },
            new Dictionary<string, RateLimitTierOptions>());

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(503);
    }

    private static RateLimitConfigAdminService CreateService(IServiceProvider provider) =>
        new(
            new StubConfigProvider(new GatewayConfigSnapshot()),
            new StubScopeFactory(provider),
            NullLogger<RateLimitConfigAdminService>.Instance);

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }

    private sealed class StubScopeFactory(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope(provider);

        private sealed class StubScope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;

            public void Dispose()
            {
            }
        }
    }

    private sealed class StubServiceProvider(
        IRateLimitSettingsRepository? repository,
        IGatewayConfigRefresher? refresher) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IRateLimitSettingsRepository))
            {
                return repository;
            }

            if (serviceType == typeof(IGatewayConfigRefresher))
            {
                return refresher;
            }

            return null;
        }
    }

    private sealed class RecordingRepository : IRateLimitSettingsRepository
    {
        public RateLimitPolicy? SavedDefault { get; private set; }

        public IReadOnlyDictionary<string, RateLimitPolicy>? SavedPlans { get; private set; }

        public bool? SavedEnabled { get; private set; }

        public Task SaveAsync(
            bool enabled,
            RateLimitPolicy defaultTier,
            IReadOnlyDictionary<string, RateLimitPolicy> plans,
            CancellationToken cancellationToken = default)
        {
            SavedEnabled = enabled;
            SavedDefault = defaultTier;
            SavedPlans = plans;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRefresher : IGatewayConfigRefresher
    {
        public bool Refreshed { get; private set; }

        public Task RefreshNowAsync(CancellationToken cancellationToken = default)
        {
            Refreshed = true;
            return Task.CompletedTask;
        }
    }
}
