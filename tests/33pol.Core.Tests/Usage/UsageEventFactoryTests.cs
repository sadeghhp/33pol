using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Core.Tests.Usage;

public sealed class UsageEventFactoryTests
{
    [Fact]
    public void FromInference_TenantWithCostCenter_SetsCostCenterOnEvent()
    {
        var tenant = new TenantContext
        {
            TenantId = Guid.NewGuid().ToString(),
            ApiKeyId = Guid.NewGuid().ToString(),
            CostCenter = "eng-platform",
            Role = ApiKeyRole.Inference,
        };

        var usage = UsageEventFactory.FromInference(
            "req_123",
            "gpt-4o-mini",
            promptTokens: 100,
            completionTokens: 50,
            durationMs: 42.5,
            tenant);

        usage.CostCenter.Should().Be("eng-platform");
        usage.TenantId.Should().Be(tenant.TenantId);
        usage.ApiKeyId.Should().Be(tenant.ApiKeyId);
    }

    [Fact]
    public void FromInference_NoTenant_LeavesCostCenterNull()
    {
        var usage = UsageEventFactory.FromInference(
            "req_anon",
            "gpt-4o-mini",
            promptTokens: 1,
            completionTokens: 1,
            durationMs: 10);

        usage.CostCenter.Should().BeNull();
        usage.TenantId.Should().BeNull();
    }

    [Fact]
    public void WithCostCenter_PrefersExistingCostCenter()
    {
        var tenant = new TenantContext
        {
            TenantId = Guid.NewGuid().ToString(),
            ApiKeyId = Guid.NewGuid().ToString(),
            CostCenter = "finance",
            Role = ApiKeyRole.Inference,
        };

        var original = new UsageEvent
        {
            RequestId = "req_456",
            ModelId = "gpt-4o",
            PromptTokens = 10,
            CompletionTokens = 20,
            DurationMs = 5,
            CostCenter = "override",
            TimestampUtc = DateTimeOffset.UtcNow,
        };

        var enriched = UsageEventFactory.WithCostCenter(original, tenant);

        enriched.CostCenter.Should().Be("override");
    }

    [Fact]
    public void WithCostCenter_FillsMissingCostCenterFromTenant()
    {
        var tenant = new TenantContext
        {
            TenantId = Guid.NewGuid().ToString(),
            ApiKeyId = Guid.NewGuid().ToString(),
            CostCenter = "research",
            Role = ApiKeyRole.Inference,
        };

        var original = UsageEventFactory.FromInference("req_789", "gpt-4o", 1, 1, 1);

        var enriched = UsageEventFactory.WithCostCenter(original, tenant);

        enriched.CostCenter.Should().Be("research");
        enriched.TenantId.Should().Be(tenant.TenantId);
    }
}
