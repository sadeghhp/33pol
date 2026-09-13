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

        var policy = resolver.Resolve(null, null, null);

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

        var policy = resolver.Resolve("enterprise", null, null);

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

        resolver.Resolve("ENTERPRISE", null, null).Rpm.Should().Be(500);
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

        resolver.Resolve("standard", "tenant-a", null).Rpm.Should().Be(999);
    }

    [Fact]
    public void Resolve_ClampsNonPositiveRpmToOne()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(0, -5, -1),
        });

        var policy = resolver.Resolve(null, null, null);

        policy.Rpm.Should().Be(1);
        policy.Burst.Should().Be(0);
        policy.MaxConcurrentStreams.Should().Be(0);
    }

    /// <summary>
    /// A tenant override with rpm 0 is "cap this tenant's streams, leave its rate to the plan". It
    /// used to be floored to 1 rpm, which turned that rule into a one-request-per-minute limit.
    /// </summary>
    [Fact]
    public void Resolve_TenantOverrideWithZeroRpm_InheritsPlanRateAndAppliesItsStreamCap()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(10, 1, 1),
            Plans = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["standard"] = new(120, 20, 10),
            },
            TenantOverrides = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant-a"] = new(0, 0, 3),
            },
        });

        var policy = resolver.Resolve("standard", "tenant-a", null);

        policy.Rpm.Should().Be(120);
        policy.Burst.Should().Be(20);
        policy.MaxConcurrentStreams.Should().Be(3);
    }

    [Fact]
    public void Resolve_TenantOverrideWithZeroRpm_InheritsDefaultRateWhenTheTenantHasNoPlan()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(42, 7, 9),
            TenantOverrides = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant-a"] = new(0, 0, 2),
            },
        });

        var policy = resolver.Resolve(null, "tenant-a", null);

        policy.Rpm.Should().Be(42);
        policy.Burst.Should().Be(7);
        policy.MaxConcurrentStreams.Should().Be(2);
    }

    /// <summary>The slug spelling of an override composes exactly like the id spelling.</summary>
    [Fact]
    public void Resolve_TenantOverrideWithZeroRpm_MatchedBySlug_Composes()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(60, 5, 4),
            TenantOverrides = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["acme"] = new(0, 0, 1),
            },
        });

        var policy = resolver.Resolve(null, "11111111-1111-1111-1111-111111111111", "acme");

        policy.Should().Be(new RateLimitPolicy(60, 5, 1));
    }

    /// <summary>An override with a positive rpm is the whole tier, exactly as before.</summary>
    [Fact]
    public void Resolve_TenantOverrideWithPositiveRpm_StillReplacesTheWholeTier()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(60, 5, 4),
            TenantOverrides = new Dictionary<string, RateLimitPolicy>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant-a"] = new(5, 0, 0),
            },
        });

        resolver.Resolve(null, "tenant-a", null).Should().Be(new RateLimitPolicy(5, 0, 0));
    }

    /// <summary>Deployments that never configured an anonymous tier keep the default one.</summary>
    [Fact]
    public void ResolveAnonymous_WhenUnset_UsesDefault()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(42, 7, 3),
        });

        resolver.ResolveAnonymous().Should().Be(new RateLimitPolicy(42, 7, 3));
    }

    [Fact]
    public void ResolveAnonymous_WhenSet_UsesItsOwnTier()
    {
        var resolver = CreateResolver(
            new RateLimitsConfigSection
            {
                Default = new RateLimitPolicy(3000, 500, 256),
                Anonymous = new RateLimitPolicy(60, 20, 2),
            },
            authenticationRequired: true);

        resolver.ResolveAnonymous().Should().Be(new RateLimitPolicy(60, 20, 2));
    }

    /// <summary>A stream-cap-only anonymous tier composes with the default rate, like a tenant rule with rpm 0.</summary>
    [Fact]
    public void ResolveAnonymous_WithZeroRpm_ComposesTheDefaultRateWithItsStreamCap()
    {
        var resolver = CreateResolver(
            new RateLimitsConfigSection
            {
                Default = new RateLimitPolicy(3000, 500, 256),
                Anonymous = new RateLimitPolicy(0, 0, 2),
            },
            authenticationRequired: true);

        resolver.ResolveAnonymous().Should().Be(new RateLimitPolicy(3000, 500, 2));
    }

    /// <summary>
    /// With authentication off there is no credential anyone could have left out, so the anonymous
    /// tier does not apply and every caller keeps the default tier.
    /// </summary>
    [Fact]
    public void ResolveAnonymous_WhenAuthenticationIsNotRequired_UsesDefault()
    {
        var resolver = CreateResolver(
            new RateLimitsConfigSection
            {
                Default = new RateLimitPolicy(3000, 500, 256),
                Anonymous = new RateLimitPolicy(60, 20, 2),
            },
            authenticationRequired: false);

        resolver.ResolveAnonymous().Should().Be(new RateLimitPolicy(3000, 500, 256));
    }

    /// <summary>The anonymous tier never touches an authenticated tenant's tier.</summary>
    [Fact]
    public void Resolve_WithATenant_IgnoresTheAnonymousTier()
    {
        var resolver = CreateResolver(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(3000, 500, 256),
            Anonymous = new RateLimitPolicy(60, 20, 2),
        });

        resolver.Resolve(null, "tenant-a", null).Should().Be(new RateLimitPolicy(3000, 500, 256));
    }

    private static RateLimitPolicyResolver CreateResolver(RateLimitsConfigSection rateLimits) =>
        new(new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits }));

    private static RateLimitPolicyResolver CreateResolver(RateLimitsConfigSection rateLimits, bool authenticationRequired) =>
        new(
            new StubConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits }),
            new StubAuthState { IsAuthenticationRequired = authenticationRequired });

    private sealed class StubAuthState : IGatewayAuthenticationState
    {
        public bool IsAuthenticationRequired { get; set; }
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }
}
