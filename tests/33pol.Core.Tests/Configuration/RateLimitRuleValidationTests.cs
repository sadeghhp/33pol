using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;

namespace Pol33.Core.Tests.Configuration;

/// <summary>
/// What the admin API will and will not accept as a scoped rule. Every rejection here is a
/// configuration that would otherwise look applied and silently never fire.
/// </summary>
public sealed class RateLimitRuleValidationTests
{
    [Fact]
    public void TryValidateRules_WithWellFormedRules_Passes()
    {
        var rules = new[]
        {
            new RateLimitRuleDefinition(RateLimitScopeNames.Model, "gpt-4", 500, 50, 0),
            new RateLimitRuleDefinition(RateLimitScopeNames.TenantModel, "acme|gpt-4", 20, 0, 2),
            new RateLimitRuleDefinition(RateLimitScopeNames.Global, "*", 10_000, 0, 0),
        };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeTrue(error);
    }

    /// <summary>A rule may cap concurrency alone; that is what a zero rpm means for a scoped rule.</summary>
    [Fact]
    public void TryValidateRules_ConcurrencyOnlyRule_IsAccepted()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.Model, "gpt-4", 0, 0, 8) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeTrue(error);
    }

    /// <summary>
    /// A rule with no rate and no concurrency cap enforces nothing. Accepting it would let an
    /// operator believe a limit is in place while every request walks past it.
    /// </summary>
    [Fact]
    public void TryValidateRules_ARuleThatEnforcesNothing_IsRejected()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.Model, "gpt-4", 0, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("enforces nothing");
    }

    [Fact]
    public void TryValidateRules_AnUnknownScope_IsRejected()
    {
        var rules = new[] { new RateLimitRuleDefinition("region", "eu-west", 10, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("scope");
    }

    [Theory]
    [InlineData("acme")]           // no separator at all
    [InlineData("acme|")]          // empty model half
    [InlineData("|gpt-4")]         // empty subject half
    [InlineData("acme|gpt|4")]     // ambiguous: two separators
    public void TryValidateRules_AMalformedPairTarget_IsRejected(string target)
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.TenantModel, target, 10, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("pair");
    }

    /// <summary>
    /// Targets are stored and matched verbatim, so a padded one is a rule that can never fire — the
    /// same trap the plan-slug validator already closes.
    /// </summary>
    [Fact]
    public void TryValidateRules_ATargetWithSurroundingWhitespace_IsRejected()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.Model, " gpt-4", 10, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("whitespace");
    }

    /// <summary>
    /// Keeping the last of two rules for the same target would make the applied configuration depend
    /// on the order the client happened to serialise its list in.
    /// </summary>
    [Fact]
    public void TryValidateRules_TwoRulesForTheSameScopeAndTarget_AreRejected()
    {
        var rules = new[]
        {
            new RateLimitRuleDefinition(RateLimitScopeNames.Model, "gpt-4", 10, 0, 0),
            new RateLimitRuleDefinition(RateLimitScopeNames.Model, "GPT-4", 20, 0, 0),
        };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("more than once");
    }

    [Theory]
    [InlineData(RateLimitScopeNames.Global)]
    [InlineData(RateLimitScopeNames.AuthFailure)]
    [InlineData(RateLimitScopeNames.Anonymous)]
    public void TryValidateRules_ASingletonScopeWithARealTarget_IsRejected(string scope)
    {
        var rules = new[] { new RateLimitRuleDefinition(scope, "gpt-4", 10, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("single partition");
    }

    /// <summary>
    /// Scope recognition has always been case-insensitive, so a mis-cased spelling was accepted as a
    /// known scope — but the shape tests that decide whether a target must be <c>*</c> or a
    /// <c>subject|model</c> pair were ordinal, so the rule skipped them entirely. It was stored,
    /// returned by the GET, rendered in the console, and could never match anything.
    /// </summary>
    [Theory]
    [InlineData("Anonymous", "acme")]
    [InlineData("AUTH_FAILURE", "acme")]
    [InlineData("Global", "gpt-4")]
    public void TryValidateRules_AMisCasedSingletonScopeWithARealTarget_IsRejected(string scope, string target)
    {
        var rules = new[] { new RateLimitRuleDefinition(scope, target, 10, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("single partition");
    }

    /// <summary>The pair scopes had the same split, so a mis-cased one skipped the separator check.</summary>
    [Theory]
    [InlineData("Tenant_Model")]
    [InlineData("API_KEY_MODEL")]
    public void TryValidateRules_AMisCasedPairScopeWithoutASeparator_IsRejected(string scope)
    {
        var rules = new[] { new RateLimitRuleDefinition(scope, "acme", 10, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("pair");
    }

    /// <summary>The anonymous tier is a singleton like the auth-failure one: one rule, target <c>*</c>.</summary>
    [Fact]
    public void TryValidateRules_AnAnonymousRule_IsAccepted()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.Anonymous, "*", 60, 20, 2) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeTrue(error);
    }

    /// <summary>
    /// A separator in a non-pair target would be read as a pair somewhere downstream; reject it here
    /// rather than let it become a key nothing matches.
    /// </summary>
    [Fact]
    public void TryValidateRules_ASeparatorInANonPairTarget_IsRejected()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.Model, "gpt|4", 10, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
    }

    [Fact]
    public void TryValidateRules_PastTheRuleCeiling_IsRejected()
    {
        var rules = Enumerable
            .Range(0, RateLimitConfigValidation.MaxRules + 1)
            .Select(i => new RateLimitRuleDefinition(RateLimitScopeNames.Model, $"m{i}", 10, 0, 0))
            .ToArray();

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("exceed");
    }

    /// <summary>
    /// A tenant rule with rpm 0 inherits the plan or default rate and applies only its stream cap.
    /// </summary>
    [Fact]
    public void TryValidateRules_ATenantRuleWithZeroRpmAndNoBurst_IsAccepted()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.Tenant, "acme", 0, 0, 3) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeTrue(error);
    }

    /// <summary>
    /// A burst next to an inherited rate has no rate to refill it, so it is refused rather than
    /// silently dropped.
    /// </summary>
    [Fact]
    public void TryValidateRules_ATenantRuleWithZeroRpmAndABurst_IsRejected()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.Tenant, "acme", 0, 5, 3) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("burst");
    }

    /// <summary>
    /// A zero rpm is "this rule does not limit the rate", so there is no rate to refill a burst with —
    /// in every scope, not only <c>tenant</c>. The pair used to be accepted everywhere else and stored
    /// a bucket of <c>burst</c> tokens refilling at the engine's floor of one token a minute, so the
    /// scope was then held to one request per minute: the opposite of what the value means, and on a
    /// <c>model</c> rule that is the whole gateway's throughput for that model.
    /// </summary>
    [Theory]
    [InlineData(RateLimitScopeNames.Global, "*")]
    [InlineData(RateLimitScopeNames.Tenant, "acme")]
    [InlineData(RateLimitScopeNames.ApiKey, "key-1")]
    [InlineData(RateLimitScopeNames.Model, "gpt-4")]
    [InlineData(RateLimitScopeNames.TenantModel, "acme|gpt-4")]
    [InlineData(RateLimitScopeNames.ApiKeyModel, "key-1|gpt-4")]
    [InlineData(RateLimitScopeNames.Anonymous, "*")]
    public void TryValidateRules_AZeroRpmWithABurst_IsRejectedInEveryScope(string scope, string target)
    {
        var rules = new[] { new RateLimitRuleDefinition(scope, target, 0, 5, 3) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("burst");
    }

    /// <summary>A burst-free zero rpm still expresses "cap concurrency only" wherever concurrency applies.</summary>
    [Theory]
    [InlineData(RateLimitScopeNames.Tenant, "acme")]
    [InlineData(RateLimitScopeNames.Model, "gpt-4")]
    [InlineData(RateLimitScopeNames.Anonymous, "*")]
    public void TryValidateRules_AZeroRpmWithNoBurstAndAStreamCap_IsAccepted(string scope, string target)
    {
        var rules = new[] { new RateLimitRuleDefinition(scope, target, 0, 0, 3) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeTrue(error);
    }

    /// <summary>
    /// <c>auth_failure</c> is metered by a limiter that only ever debits a token bucket, so a rule
    /// there carrying nothing but a stream cap enforced nothing at all — and because the resolver
    /// falls back to the default tier when the auth-failure tier has no rate, it quietly widened
    /// credential guessing to whatever a paying tenant is allowed.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 5)]
    [InlineData(0, 0, 0)]
    public void TryValidateRules_AnAuthFailureRuleWithNoRate_IsRejected(int rpm, int burst, int streams)
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.AuthFailure, "*", rpm, burst, streams) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
    }

    /// <summary>A stream cap on the rate-only scope is refused rather than stored and ignored.</summary>
    [Fact]
    public void TryValidateRules_AnAuthFailureRuleWithAStreamCap_IsRejected()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.AuthFailure, "*", 60, 20, 5) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("maxConcurrentStreams");
    }

    /// <summary>
    /// Neither range check used to see a negative rpm: the tier check runs above zero and the
    /// concurrency-only check at zero, so -50 with a burst slipped through as a real bucket.
    /// </summary>
    [Theory]
    [InlineData(RateLimitScopeNames.Model)]
    [InlineData(RateLimitScopeNames.Tenant)]
    [InlineData(RateLimitScopeNames.ApiKey)]
    public void TryValidateRules_ANegativeRpm_IsRejected(string scope)
    {
        var rules = new[] { new RateLimitRuleDefinition(scope, "target", -50, 100, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("negative");
    }

    /// <summary>Null means "the caller does not manage rules", which is not an error.</summary>
    [Fact]
    public void TryValidateRules_Null_Passes()
    {
        RateLimitConfigValidation.TryValidateRules(null, out var error).Should().BeTrue(error);
    }
}
