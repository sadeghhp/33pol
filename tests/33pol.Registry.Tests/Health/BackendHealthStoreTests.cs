using FluentAssertions;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Registry.Health;

namespace Pol33.Registry.Tests.Health;

public sealed class BackendHealthStoreTests
{
    [Fact]
    public void IsBackendHealthy_BeforeFirstCheck_OptimisticMode_ReturnsTrue()
    {
        var store = CreateStore(strictMode: false);

        store.IsBackendHealthy("model-a").Should().BeTrue();
    }

    [Fact]
    public void IsBackendHealthy_BeforeFirstCheck_StrictMode_ReturnsFalse()
    {
        var store = CreateStore(strictMode: true);

        store.IsBackendHealthy("model-a").Should().BeFalse();
    }

    [Fact]
    public void SetHealth_UnhealthyEntry_ReturnsFalse()
    {
        var store = CreateStore(strictMode: false);
        store.SetHealth(new BackendHealth("model-a", "http://a", false, 503, "down", DateTimeOffset.UtcNow));

        store.IsBackendHealthy("model-a").Should().BeFalse();
    }

    private static BackendHealthStore CreateStore(bool strictMode) =>
        new(Options.Create(new GatewayOptions { HealthCheckStrictMode = strictMode }));
}
