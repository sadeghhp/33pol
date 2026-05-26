using Pol33.Core.Identity;

namespace Pol33.Core.Tests.Identity;

public sealed class ModelGrantEvaluatorTests
{
    [Fact]
    public void IsModelAllowed_NoGrants_ReturnsTrue()
    {
        ModelGrantEvaluator.IsModelAllowed([], "gpt-4").Should().BeTrue();
    }

    [Fact]
    public void IsModelAllowed_MatchingAllowGrant_ReturnsTrue()
    {
        var grants = new[]
        {
            new ModelGrantRecord(Guid.NewGuid(), Guid.NewGuid(), "gpt-4", GrantEffect.Allow),
        };

        ModelGrantEvaluator.IsModelAllowed(grants, "gpt-4").Should().BeTrue();
    }

    [Fact]
    public void IsModelAllowed_GrantsExistButNoMatch_ReturnsFalse()
    {
        var grants = new[]
        {
            new ModelGrantRecord(Guid.NewGuid(), Guid.NewGuid(), "gpt-4", GrantEffect.Allow),
        };

        ModelGrantEvaluator.IsModelAllowed(grants, "claude-3").Should().BeFalse();
    }

    [Fact]
    public void IsModelAllowed_CaseInsensitivePatternMatch_ReturnsTrue()
    {
        var grants = new[]
        {
            new ModelGrantRecord(Guid.NewGuid(), Guid.NewGuid(), "GPT-4", GrantEffect.Allow),
        };

        ModelGrantEvaluator.IsModelAllowed(grants, "gpt-4").Should().BeTrue();
    }
}
