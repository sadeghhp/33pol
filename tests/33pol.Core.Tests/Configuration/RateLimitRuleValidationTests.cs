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

    [Fact]
    public void TryValidateRules_ASingletonScopeWithARealTarget_IsRejected()
    {
        var rules = new[] { new RateLimitRuleDefinition(RateLimitScopeNames.Global, "gpt-4", 10, 0, 0) };

        RateLimitConfigValidation.TryValidateRules(rules, out var error).Should().BeFalse();
        error.Should().Contain("single partition");
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

    /// <summary>Null means "the caller does not manage rules", which is not an error.</summary>
    [Fact]
    public void TryValidateRules_Null_Passes()
    {
        RateLimitConfigValidation.TryValidateRules(null, out var error).Should().BeTrue(error);
    }
}
