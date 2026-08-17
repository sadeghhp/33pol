using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Core.Usage;

namespace Pol33.Core.Tests.Usage;

public sealed class RecentRequestUsageMapperTests
{
    private static UsageEvent Event(UsageTokenSource source = UsageTokenSource.Split) => new()
    {
        RequestId = "req-1",
        ModelId = "m1",
        PromptTokens = source == UsageTokenSource.TotalOnly ? 0 : 120,
        CompletionTokens = source == UsageTokenSource.TotalOnly ? 0 : 30,
        TotalTokens = source == UsageTokenSource.TotalOnly ? 150 : 0,
        TokenSource = source,
    };

    [Fact]
    public void FromUsageEvent_CarriesTokensAndStatusWithoutCosts()
    {
        var usage = RecentRequestUsageMapper.FromUsageEvent(Event(), RecentRequestUsage.StatusPending);

        usage.PromptTokens.Should().Be(120);
        usage.CompletionTokens.Should().Be(30);
        usage.TotalTokens.Should().Be(150);
        usage.TokenSource.Should().Be("split");
        usage.InputCost.Should().BeNull();
        usage.TotalCost.Should().BeNull();
        usage.PricingStatus.Should().Be("pending");
    }

    [Fact]
    public void FromUsageEvent_TotalOnly_UsesTheCombinedFigure()
    {
        var usage = RecentRequestUsageMapper.FromUsageEvent(Event(UsageTokenSource.TotalOnly), RecentRequestUsage.StatusUnpriced);

        usage.TotalTokens.Should().Be(150);
        usage.TokenSource.Should().Be("totalOnly");
    }

    [Fact]
    public void FromBillingEvent_Priced_ReportsBothSidesAndCurrency()
    {
        var source = Event();
        var record = new BillingEventRecord(
            Guid.NewGuid(), "req-1", null, null, "m1", "FIN-1", 120, 30,
            InputCost: 0.00036m, OutputCost: 0.00045m, TotalCost: 0.00081m,
            DurationMs: 250, RecordedAt: DateTimeOffset.UtcNow);

        var usage = RecentRequestUsageMapper.FromBillingEvent(record, source, "EUR");

        usage.PricingStatus.Should().Be("priced");
        usage.InputCost.Should().Be(0.00036m);
        usage.OutputCost.Should().Be(0.00045m);
        usage.TotalCost.Should().Be(0.00081m);
        usage.Currency.Should().Be("EUR");
        usage.TotalTokens.Should().Be(150);
    }

    [Fact]
    public void FromBillingEvent_WithoutRateCard_IsUnpricedNotPending()
    {
        var record = new BillingEventRecord(
            Guid.NewGuid(), "req-1", null, null, "m1", null, 120, 30,
            InputCost: null, OutputCost: null, TotalCost: null,
            DurationMs: 250, RecordedAt: DateTimeOffset.UtcNow);

        var usage = RecentRequestUsageMapper.FromBillingEvent(record, Event(), currency: "USD");

        usage.PricingStatus.Should().Be("unpriced");
        usage.Currency.Should().BeNull();
        usage.PromptTokens.Should().Be(120);
    }
}
