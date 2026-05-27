using Pol33.Core.Identity;

namespace Pol33.Core.Tests.Identity;

public sealed class ModelGrantIntersectionTests
{
    private static ModelGrantRecord TenantGrant(string pattern) =>
        new(Guid.NewGuid(), Guid.NewGuid(), pattern, GrantEffect.Allow);

    private static ApiKeyModelGrantRecord KeyGrant(string pattern) =>
        new(Guid.NewGuid(), Guid.NewGuid(), pattern, GrantEffect.Allow);

    [Fact]
    public void IsModelAllowed_OpenTenantNoKeyGrants_DeniesAll()
    {
        ModelGrantEvaluator.IsModelAllowed([], [], "any-model").Should().BeFalse();
    }

    [Fact]
    public void IsModelAllowed_TenantRestrictedNoKeyGrants_DeniesAll()
    {
        var tenant = new[] { TenantGrant("gpt-local") };
        ModelGrantEvaluator.IsModelAllowed(tenant, [], "gpt-local").Should().BeFalse();
        ModelGrantEvaluator.IsModelAllowed(tenant, [], "other").Should().BeFalse();
    }

    [Fact]
    public void IsModelAllowed_TenantOpenKeyAllowlist_OnlyListedModels()
    {
        var key = new[] { KeyGrant("gpt-local") };
        ModelGrantEvaluator.IsModelAllowed([], key, "gpt-local").Should().BeTrue();
        ModelGrantEvaluator.IsModelAllowed([], key, "other").Should().BeFalse();
    }

    [Fact]
    public void IsModelAllowed_TenantAndKeyRestricted_Intersection()
    {
        var tenant = new[] { TenantGrant("a"), TenantGrant("b") };
        var key = new[] { KeyGrant("b") };
        ModelGrantEvaluator.IsModelAllowed(tenant, key, "b").Should().BeTrue();
        ModelGrantEvaluator.IsModelAllowed(tenant, key, "a").Should().BeFalse();
    }

    [Fact]
    public void IsModelAllowed_KeyAllowsButTenantDenies_Denied()
    {
        var tenant = new[] { TenantGrant("a") };
        var key = new[] { KeyGrant("b") };
        ModelGrantEvaluator.IsModelAllowed(tenant, key, "b").Should().BeFalse();
    }
}
