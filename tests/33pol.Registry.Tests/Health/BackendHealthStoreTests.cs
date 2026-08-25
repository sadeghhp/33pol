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

    [Fact]
    public void GetAllHealth_ReturnsStoredEntries()
    {
        var store = CreateStore(strictMode: false);
        store.SetHealth(new BackendHealth("model-a", "http://a", true, 200, null, DateTimeOffset.UtcNow));

        store.GetAllHealth().Should().ContainKey("model-a");
    }

    [Fact]
    public void RetainOnly_DropsEntriesForModelsNoLongerRegistered()
    {
        var store = CreateStore(strictMode: true);
        store.SetHealth(new BackendHealth("model-a", "http://a", true, 200, null, DateTimeOffset.UtcNow));
        store.SetHealth(new BackendHealth("model-b", "http://b", true, 200, null, DateTimeOffset.UtcNow));

        store.RetainOnly(["MODEL-A"]);

        store.GetAllHealth().Keys.Should().BeEquivalentTo("model-a");
        store.GetHealth("model-b").Should().BeNull();
        store.IsBackendHealthy("model-b").Should().BeFalse("strict mode must not answer for a model that no longer exists");
    }

    private static BackendHealthStore CreateStore(bool strictMode) =>
        new(Options.Create(new GatewayOptions { HealthCheckStrictMode = strictMode }));

    [Fact]
    public void SetHealth_TracksWhenTheStateLastFlipped()
    {
        var store = CreateStore(strictMode: false);
        var t0 = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        store.SetHealth(new BackendHealth("m", "http://x", true, 200, null, t0));
        store.GetHealth("m")!.LastTransitionUtc.Should().Be(t0, "the first observation starts the clock");

        store.SetHealth(new BackendHealth("m", "http://x", true, 200, null, t0.AddSeconds(30)));
        store.GetHealth("m")!.LastTransitionUtc.Should().Be(t0, "same state, the stamp is kept");

        store.SetHealth(new BackendHealth("m", "http://x", false, 503, "boom", t0.AddSeconds(60)));
        var flipped = store.GetHealth("m")!;
        flipped.IsHealthy.Should().BeFalse();
        flipped.LastTransitionUtc.Should().Be(t0.AddSeconds(60));
    }
}
