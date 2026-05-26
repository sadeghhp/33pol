using Pol33.Billing.RateCards;
using Pol33.Billing.Usage;
using Pol33.Core.Billing;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingEventFactoryTests
{
    [Fact]
    public void FromUsageEvent_PreservesCostCenterAndCosts()
    {
        var tenantId = Guid.NewGuid();
        var apiKeyId = Guid.NewGuid();
        var usage = UsageEventFactory.FromInference(
            "req_billing_1",
            "gpt-4o-mini",
            promptTokens: 1_000,
            completionTokens: 500,
            durationMs: 120,
            new TenantContext
            {
                TenantId = tenantId.ToString(),
                ApiKeyId = apiKeyId.ToString(),
                CostCenter = "eng-platform",
                Role = ApiKeyRole.Inference,
            });

        var rateCard = new RateCardRecord(
            Guid.NewGuid(),
            "mini",
            "Mini",
            "gpt-4o-mini",
            0.15m,
            0.60m,
            "USD",
            DateTimeOffset.UtcNow,
            null,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var costs = new RateCardCostCalculator().Calculate(rateCard, usage.PromptTokens, usage.CompletionTokens);

        var billingEvent = BillingEventFactory.FromUsageEvent(usage, costs);

        billingEvent.RequestId.Should().Be("req_billing_1");
        billingEvent.CostCenter.Should().Be("eng-platform");
        billingEvent.TenantId.Should().Be(tenantId);
        billingEvent.ApiKeyId.Should().Be(apiKeyId);
        billingEvent.TotalCost.Should().Be(costs.TotalCost);
    }

    [Fact]
    public void FromUsageEvent_EmptyRequestId_Throws()
    {
        var invalid = new UsageEvent
        {
            RequestId = "  ",
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 1,
            DurationMs = 1,
        };

        var act = () => BillingEventFactory.FromUsageEvent(invalid);

        act.Should().Throw<ArgumentException>();
    }
}
