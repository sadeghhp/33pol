using Pol33.Billing.Usage;
using Pol33.Core.Billing;
using Pol33.Core.Models;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingEventMapperTests
{
    [Fact]
    public void FromUsageEvent_DelegatesToFactory()
    {
        var usage = new UsageEvent
        {
            RequestId = "req-mapper",
            TenantId = Guid.NewGuid().ToString(),
            ModelId = "gpt-4o",
            PromptTokens = 3,
            CompletionTokens = 2,
            DurationMs = 10,
            TimestampUtc = DateTimeOffset.UtcNow,
        };

        var costs = new BillingCostBreakdown(0.01m, 0.02m, 0.03m, "USD");
        var record = BillingEventMapper.FromUsageEvent(usage, costs);

        record.RequestId.Should().Be("req-mapper");
        record.TotalCost.Should().Be(0.03m);
    }
}
