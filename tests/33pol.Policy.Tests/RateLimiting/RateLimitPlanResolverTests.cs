using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>
/// Which rules apply to a request, in which order, and what happens when several sources could name
/// a tier for the same caller.
/// </summary>
public sealed class RateLimitPlanResolverTests
{
    private static readonly RateLimitSubject Acme =
        new("acme", null, "pro", "key-1", "acme");

    [Fact]
    public void Resolve_WithNothingConfigured_ProducesTheTenantRuleAlone()
    {
        var plan = Create(new RateLimitsConfigSection { Default = new RateLimitPolicy(60, 10, 5) })
            .Resolve(Acme, modelId: "gpt-4");

        plan.Rules.Should().ContainSingle();
        plan.Rules[0].Scope.Should().Be(RateLimitScope.Tenant);
        plan.Rules[0].PartitionKey.Should().Be(RateLimitKeys.Tenant("acme"));
        plan.ModelRules.Length.Should().Be(0);
    }

    /// <summary>
    /// The scopes compose: every one that is configured produces a rule, and they are ordered with
    /// the model-independent ones first so the first stage can be evaluated before the body is read.
    /// </summary>
    [Fact]
    public void Resolve_WithEveryScopeConfigured_ProducesAllSixInStageOrder()
    {
        var resolver = Create(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(60, 10, 5),
            Global = new RateLimitPolicy(10_000, 0, 0),
            ApiKeys = Map(("key-1", new RateLimitPolicy(30, 0, 0))),
            Models = Map(("gpt-4", new RateLimitPolicy(500, 0, 0))),
            TenantModels = Map(("acme|gpt-4", new RateLimitPolicy(20, 0, 0))),
            ApiKeyModels = Map(("key-1|gpt-4", new RateLimitPolicy(10, 0, 0))),
        });

        var plan = resolver.Resolve(Acme, modelId: "gpt-4");

        plan.Rules.Select(r => r.Scope).Should().Equal(
            RateLimitScope.Global,
            RateLimitScope.Tenant,
            RateLimitScope.ApiKey,
            RateLimitScope.Model,
            RateLimitScope.TenantModel,
            RateLimitScope.ApiKeyModel);

        plan.IdentityRules.Length.Should().Be(3, "the first three need nothing from the body");
        plan.ModelRules.Length.Should().Be(3);
    }

    /// <summary>
    /// Every scope namespaces its own bucket key. Without that, a tenant whose id happened to equal
    /// a model id would share that model's bucket.
    /// </summary>
    [Fact]
    public void Resolve_KeysAreNamespacedPerScope()
    {
        var resolver = Create(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(60, 10, 5),
            Models = Map(("acme", new RateLimitPolicy(500, 0, 0))),
        });

        // A tenant called "acme" and a model called "acme".
        var plan = resolver.Resolve(new RateLimitSubject("acme", null, null, null, "acme"), modelId: "acme");

        plan.Rules.Select(r => r.PartitionKey).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Precedence exists in exactly one place: the tenant scope, where an override beats the plan,
    /// which beats the default.
    /// </summary>
    [Theory]
    [InlineData("acme", "pro", 5)]     // per-tenant override
    [InlineData("other", "pro", 50)]   // plan tier
    [InlineData("other", "free", 60)]  // default
    public void Resolve_TenantScope_AppliesOverrideThenPlanThenDefault(
        string tenantId,
        string planSlug,
        int expectedRpm)
    {
        var resolver = Create(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(60, 0, 0),
            Plans = Map(("pro", new RateLimitPolicy(50, 0, 0))),
            TenantOverrides = Map(("acme", new RateLimitPolicy(5, 0, 0))),
        });

        var plan = resolver.Resolve(new RateLimitSubject(tenantId, null, planSlug, null, tenantId), modelId: null);

        plan.Rules.Single(r => r.Scope == RateLimitScope.Tenant).Policy.Rpm.Should().Be(expectedRpm);
    }

    /// <summary>
    /// A resolved plan is reused, so the hot path is a lookup rather than a rebuild — and an admin
    /// edit, which bumps the config version, invalidates it immediately rather than after a TTL.
    /// </summary>
    [Fact]
    public void Resolve_SameSubjectAndVersion_ReturnsTheCachedPlan()
    {
        var provider = new MutableConfigProvider(new GatewayConfigSnapshot
        {
            Version = 1,
            RateLimits = new RateLimitsConfigSection { Default = new RateLimitPolicy(60, 0, 0) },
        });
        var resolver = new RateLimitPlanResolver(provider);

        var first = resolver.Resolve(Acme, "gpt-4");
        resolver.Resolve(Acme, "gpt-4").Should().BeSameAs(first);

        provider.Current = new GatewayConfigSnapshot
        {
            Version = 2,
            RateLimits = new RateLimitsConfigSection { Default = new RateLimitPolicy(5, 0, 0) },
        };

        var afterEdit = resolver.Resolve(Acme, "gpt-4");
        afterEdit.Should().NotBeSameAs(first);
        afterEdit.Rules[0].Policy.Rpm.Should().Be(5);
    }

    /// <summary>
    /// The request path uses this to decide whether it needs to parse the body at all, so a gateway
    /// with no per-model rules must answer false and pay nothing for the feature.
    /// </summary>
    [Fact]
    public void HasModelScopedRules_IsFalseUntilAModelScopedRuleExists()
    {
        Create(new RateLimitsConfigSection()).HasModelScopedRules().Should().BeFalse();

        Create(new RateLimitsConfigSection { Models = Map(("gpt-4", new RateLimitPolicy(1, 0, 0))) })
            .HasModelScopedRules().Should().BeTrue();

        Create(new RateLimitsConfigSection { ApiKeyModels = Map(("k|m", new RateLimitPolicy(1, 0, 0))) })
            .HasModelScopedRules().Should().BeTrue();
    }

    /// <summary>An anonymous caller has no tenant or key, so only the scopes it can be identified by apply.</summary>
    [Fact]
    public void Resolve_AnonymousSubject_SkipsTheKeyAndCombinedScopes()
    {
        var resolver = Create(new RateLimitsConfigSection
        {
            Default = new RateLimitPolicy(60, 0, 0),
            ApiKeys = Map(("key-1", new RateLimitPolicy(1, 0, 0))),
            TenantModels = Map(("acme|gpt-4", new RateLimitPolicy(1, 0, 0))),
        });

        var plan = resolver.Resolve(new RateLimitSubject(null, null, null, null, "anon:203.0.113.7"), "gpt-4");

        plan.Rules.Should().ContainSingle();
        plan.Rules[0].PartitionKey.Should().Be(RateLimitKeys.Tenant("anon:203.0.113.7"));
    }

    private static RateLimitPlanResolver Create(RateLimitsConfigSection rateLimits) =>
        new(new MutableConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits }));

    private static IReadOnlyDictionary<string, RateLimitPolicy> Map(
        params (string Key, RateLimitPolicy Policy)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Policy, StringComparer.OrdinalIgnoreCase);

    private sealed class MutableConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; set; } = snapshot;
    }
}
