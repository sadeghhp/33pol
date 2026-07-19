using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

public sealed class RateLimitPolicyResolverTests
{
    [Fact]
    public void Resolve_NoPlanOrTenant_UsesDefault()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(42, 7, 3),
        });

        var policy = resolver.Resolve(null, null);

        policy.Rpm.Should().Be(42);
        policy.Burst.Should().Be(7);
        policy.MaxConcurrentStreams.Should().Be(3);
    }

    [Fact]
    public void Resolve_PlanSlug_UsesPlanTier()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(10, 1, 1),
            Plans = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["enterprise"] = new(500, 50, 25),
            },
        });

        var policy = resolver.Resolve("enterprise", null);

        policy.Rpm.Should().Be(500);
        policy.Burst.Should().Be(50);
        policy.MaxConcurrentStreams.Should().Be(25);
    }

    [Fact]
    public void Resolve_PlanSlug_IsCaseInsensitive()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(10, 1, 1),
            Plans = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["enterprise"] = new(500, 50, 25),
            },
        });

        resolver.Resolve("ENTERPRISE", null).Rpm.Should().Be(500);
    }

    [Fact]
    public void Resolve_TenantOverride_TakesPrecedenceOverPlan()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(10, 1, 1),
            Plans = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["standard"] = new(100, 10, 5),
            },
            TenantOverrides = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant-a"] = new(999, 99, 9),
            },
        });

        resolver.Resolve("standard", "tenant-a").Rpm.Should().Be(999);
    }

    [Fact]
    public void Resolve_ClampsNonPositiveRpmToOne()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(0, -5, -1),
        });

        var policy = resolver.Resolve(null, null);

        policy.Rpm.Should().Be(1);
        policy.Burst.Should().Be(0);
        policy.MaxConcurrentStreams.Should().Be(0);
    }

    private static RateLimitPolicyResolver CreateResolver(RateLimitsConfigSection rateLimits) =>
        new(new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits }));

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
