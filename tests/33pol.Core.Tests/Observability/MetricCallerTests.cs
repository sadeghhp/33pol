using Pol33.Core.Identity;
using Pol33.Core.Observability;

namespace Pol33.Core.Tests.Observability;

public sealed class MetricCallerTests
{
    [Fact]
    public void From_NoTenantContext_IsAnonymous()
    {
        MetricCaller.From(tenant: null).Should().Be(new MetricCaller("anonymous", "(none)"));
    }

    [Fact]
    public void From_AuthenticatedKey_UsesTheSlugAndTheLabel_NeverTheIds()
    {
        var caller = MetricCaller.From(new TenantContext
        {
            TenantId = "7b0a3a52-2f0c-4a4b-9a57-0d2d5f0c1e11",
            ApiKeyId = "0f3c1d1e-6f0e-4a55-8d39-6b2c8f1a9c22",
            TenantSlug = "fanus",
            ApiKeyLabel = "Fanus-MMT-Campaign",
        });

        caller.Should().Be(new MetricCaller("fanus", "Fanus-MMT-Campaign"));
    }

    /// <summary>
    /// A key issued without a label still has to land somewhere readable. It shares a series with
    /// the tenant's other unlabeled keys rather than exporting its id or prefix.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void From_KeyWithoutALabel_IsUnlabeled(string? label)
    {
        MetricCaller.From("fanus", label).Should().Be(new MetricCaller("fanus", "(unlabeled)"));
    }

    [Fact]
    public void From_TenantWithoutASlug_IsUnknown_NotAnonymous()
    {
        MetricCaller.From(tenantSlug: null, apiKeyLabel: "batch").Tenant.Should().Be("unknown");
    }

    [Fact]
    public void From_TrimsAndBoundsTheLabel()
    {
        var caller = MetricCaller.From("  fanus ", " " + new string('k', 200));

        caller.Tenant.Should().Be("fanus");
        caller.Key.Should().HaveLength(MetricCaller.MaxLabelLength);
    }

    [Fact]
    public void Default_IsNotSpecified()
    {
        default(MetricCaller).IsSpecified.Should().BeFalse();
        MetricCaller.Anonymous.IsSpecified.Should().BeTrue();
    }
}
