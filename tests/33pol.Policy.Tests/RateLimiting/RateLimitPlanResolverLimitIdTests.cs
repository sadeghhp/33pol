using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Policy.RateLimiting;

namespace Pol33.Policy.Tests.RateLimiting;

/// <summary>
/// Every rule in a plan names the configured control it came from. That id is what the per-limit
/// usage report counts under and what the console joins a rule row to, so it has to be the control
/// an operator would edit — not the bucket, and never a display name.
/// </summary>
public sealed class RateLimitPlanResolverLimitIdTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly RateLimitSubject Acme = new(
        "11111111-1111-1111-1111-111111111111",
        "Acme",
        "Pro",
        "KEY-1",
        "11111111-1111-1111-1111-111111111111");

    [Fact]
    public void TenantScope_WithNoPlanOrOverride_IsTheDefaultLimit()
    {
        var rule = Resolve(new RateLimitsConfigSection { Default = new(60, 0, 0) }, Acme with { PlanSlug = null }).Rules.Single();

        rule.LimitId.Should().Be("default");
        rule.StreamLimitId.Should().Be("default");
    }

    [Fact]
    public void TenantScope_OnAConfiguredPlan_IsThatPlansLimit_LowerCased()
    {
        var rule = Resolve(
            new RateLimitsConfigSection { Default = new(60, 0, 0), Plans = Map(("pro", new(600, 0, 0))) },
            Acme).Rules.Single();

        rule.LimitId.Should().Be("plan:pro");
    }

    [Fact]
    public void TenantScope_OnAnUnknownPlan_FallsToDefault()
    {
        var rule = Resolve(new RateLimitsConfigSection { Default = new(60, 0, 0) }, Acme).Rules.Single();

        rule.LimitId.Should().Be("default", "a plan with no tier is held to the default, so that is the number to edit");
    }

    /// <summary>A rule written against the slug and one written against the id are different rules.</summary>
    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("acme")]
    public void TenantScope_WithAnOverride_IsThatRule_UnderTheTargetItWasWrittenAgainst(string target)
    {
        var rule = Resolve(
            new RateLimitsConfigSection
            {
                Default = new(60, 0, 0),
                Plans = Map(("pro", new(600, 0, 0))),
                TenantOverrides = Map((target, new(30, 0, 0))),
            },
            Acme).Rules.Single();

        rule.LimitId.Should().Be("tenant:" + target);
        rule.StreamLimitId.Should().Be("tenant:" + target);
    }

    /// <summary>
    /// An override with no rate keeps the plan's rate and replaces only the cap, so the two controls
    /// on that one bucket are different rows in the report.
    /// </summary>
    [Fact]
    public void TenantScope_WithAStreamsOnlyOverride_SplitsRateAndStreamsBetweenTwoControls()
    {
        var rule = Resolve(
            new RateLimitsConfigSection
            {
                Default = new(60, 0, 0),
                Plans = Map(("pro", new(600, 0, 0))),
                TenantOverrides = Map(("acme", new(0, 0, 4))),
            },
            Acme).Rules.Single();

        rule.Policy.Rpm.Should().Be(600);
        rule.Policy.MaxConcurrentStreams.Should().Be(4);
        rule.LimitId.Should().Be("plan:pro");
        rule.StreamLimitId.Should().Be("tenant:acme");
    }

    [Fact]
    public void EveryScopedRule_CarriesItsOwnIdentity()
    {
        var plan = Resolve(
            new RateLimitsConfigSection
            {
                Default = new(60, 0, 0),
                Global = new(5000, 0, 0),
                ApiKeys = Map(("key-1", new(20, 0, 0))),
                Models = Map(("GPT-4", new(100, 0, 0))),
                TenantModels = Map(("acme|gpt-4", new(10, 0, 0))),
                ApiKeyModels = Map(("key-1|gpt-4", new(5, 0, 0))),
            },
            Acme,
            "gpt-4");

        plan.Rules.Select(r => r.LimitId).Should().Equal(
            "global:*",
            "default",
            "api_key:key-1",
            "model:gpt-4",
            "tenant_model:acme|gpt-4",
            "api_key_model:key-1|gpt-4");
        plan.Rules.Should().OnlyContain(r => !r.AnonymousBucket);
    }

    [Fact]
    public void AdaptiveScaling_KeepsTheIdentity_AndReportsBothRates()
    {
        var resolver = new RateLimitPlanResolver(
            Provider(new RateLimitsConfigSection
            {
                Default = new(1000, 0, 0),
                Models = Map(("gpt-4", new(100, 0, 0))),
                AdaptiveEnabled = true,
            }),
            new HalvingGovernor());

        var rule = resolver.Resolve(Acme, "gpt-4").ModelRules[0];

        rule.LimitId.Should().Be("model:gpt-4");
        rule.ConfiguredRpm.Should().Be(100);
        rule.Policy.Rpm.Should().Be(50);
    }

    /// <summary>
    /// A window changes the number, not the control: the same id, enforcing the window's tier, which
    /// is what the report then shows as configured.
    /// </summary>
    [Fact]
    public void AScheduledWindow_ChangesTheTier_NotTheIdentity()
    {
        var stored = new RateLimitsConfigSection
        {
            Default = new(60, 0, 0),
            TenantOverrides = Map(("acme", new(300, 0, 0))),
            Schedules = new Dictionary<string, IReadOnlyList<RateLimitWindowDefinition>>(StringComparer.OrdinalIgnoreCase)
            {
                ["tenant:acme"] =
                [
                    new("lunch", RateLimitWindowKinds.Weekly, 40, 0, 0, Days: ["mon"], Start: "11:00", End: "13:00", TimeZone: "UTC"),
                ],
            },
        };

        var (effective, _) = RateLimitScheduleProjection.Project(stored, Monday, effectiveVersion: 1);
        var rule = Resolve(effective, Acme).Rules.Single();

        rule.LimitId.Should().Be("tenant:acme");
        rule.Policy.Rpm.Should().Be(40);
        rule.ConfiguredRpm.Should().Be(40);
    }

    [Fact]
    public void Anonymous_WithAnAnonymousRate_IsTheAnonymousLimit_AndUsesTheModelsAnonymousBucket()
    {
        var plan = Resolve(
            new RateLimitsConfigSection
            {
                Default = new(60, 0, 0),
                Anonymous = new(10, 0, 0),
                Models = Map(("gpt-4", new(100, 0, 0))),
            },
            new RateLimitSubject(null, null, null, null, "anon:10.0.0.0"),
            "gpt-4",
            authenticationRequired: true);

        plan.Rules[0].LimitId.Should().Be("anonymous:*");
        plan.ModelRules[0].LimitId.Should().Be("model:gpt-4");
        plan.ModelRules[0].AnonymousBucket.Should().BeTrue();
    }

    [Fact]
    public void Anonymous_WithNoAnonymousRate_IsCountedUnderDefault()
    {
        var plan = Resolve(
            new RateLimitsConfigSection { Default = new(60, 0, 0) },
            new RateLimitSubject(null, null, null, null, "anon:10.0.0.0"),
            authenticationRequired: true);

        plan.Rules.Single().LimitId.Should().Be("default");
    }

    private static RateLimitPlan Resolve(
        RateLimitsConfigSection rateLimits,
        RateLimitSubject subject,
        string? modelId = null,
        bool authenticationRequired = false) =>
        new RateLimitPlanResolver(Provider(rateLimits), governor: null, new AuthState(authenticationRequired))
            .Resolve(subject, modelId);

    private static IGatewayConfigProvider Provider(RateLimitsConfigSection rateLimits) =>
        new FixedConfigProvider(new GatewayConfigSnapshot { RateLimits = rateLimits });

    private static IReadOnlyDictionary<string, RateLimitPolicy> Map(params (string Key, RateLimitPolicy Policy)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Policy, StringComparer.OrdinalIgnoreCase);

    private sealed class FixedConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current { get; } = snapshot;
    }

    private sealed class AuthState(bool required) : IGatewayAuthenticationState
    {
        public bool IsAuthenticationRequired { get; } = required;
    }

    private sealed class HalvingGovernor : IAdaptiveRateLimitGovernor
    {
        public bool IsEnabled => true;

        public double GetModelFactor(string modelId) => 0.5;

        public int GetRetryAfterSeconds(string partitionKey, int baseRetryAfterSeconds, DateTimeOffset now) => baseRetryAfterSeconds;

        public void RecordOutcome(string partitionKey, bool admitted, DateTimeOffset now)
        {
        }

        public void Evaluate(DateTimeOffset now)
        {
        }

        public AdaptiveRateLimitSnapshot Snapshot() => AdaptiveRateLimitSnapshot.Disabled;
    }
}
