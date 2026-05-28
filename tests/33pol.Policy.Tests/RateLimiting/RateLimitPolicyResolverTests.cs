using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

public sealed class RateLimitPolicyResolverTests
{
    [Fact]
    public void Resolve_NoPlanOrTenant_UsesDefault()
    {
        var resolver = CreateResolver(new RateLimitingOptions
        {
            Default = new RateLimitTierOptions { Rpm = 42, Burst = 7, MaxConcurrentStreams = 3 },
        });

        var policy = resolver.Resolve(null, null);

        policy.Rpm.Should().Be(42);
        policy.Burst.Should().Be(7);
        policy.MaxConcurrentStreams.Should().Be(3);
    }

    [Fact]
    public void Resolve_PlanSlug_UsesPlanTier()
    {
        var resolver = CreateResolver(new RateLimitingOptions
        {
            Default = new RateLimitTierOptions { Rpm = 10, Burst = 1, MaxConcurrentStreams = 1 },
            Plans = new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["enterprise"] = new() { Rpm = 500, Burst = 50, MaxConcurrentStreams = 25 },
            },
        });

        var policy = resolver.Resolve("enterprise", null);

        policy.Rpm.Should().Be(500);
        policy.Burst.Should().Be(50);
        policy.MaxConcurrentStreams.Should().Be(25);
    }

    [Fact]
    public void Resolve_TenantOverride_TakesPrecedenceOverPlan()
    {
        var resolver = CreateResolver(new RateLimitingOptions
        {
            Default = new RateLimitTierOptions { Rpm = 10, Burst = 1, MaxConcurrentStreams = 1 },
            Plans = new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["standard"] = new() { Rpm = 100, Burst = 10, MaxConcurrentStreams = 5 },
            },
            Tenants = new Dictionary<string, RateLimitTierOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant-a"] = new() { Rpm = 999, Burst = 99, MaxConcurrentStreams = 9 },
            },
        });

        var policy = resolver.Resolve("standard", "tenant-a");

        policy.Rpm.Should().Be(999);
    }

    private static RateLimitPolicyResolver CreateResolver(RateLimitingOptions options) =>
        new(new TestOptionsMonitor(options));

    private sealed class TestOptionsMonitor(RateLimitingOptions value) : IOptionsMonitor<RateLimitingOptions>
    {
        public RateLimitingOptions CurrentValue { get; private set; } = value;

        public RateLimitingOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<RateLimitingOptions, string?> listener) => null;
    }
}
