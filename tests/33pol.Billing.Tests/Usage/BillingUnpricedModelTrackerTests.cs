using Pol33.Billing.Usage;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingUnpricedModelTrackerTests
{
    [Fact]
    public void TryMarkWarned_ReturnsTrueOnce_PerModel()
    {
        var tracker = new BillingUnpricedModelTracker();

        tracker.TryMarkWarned("gpt-4o").Should().BeTrue();
        tracker.TryMarkWarned("gpt-4o").Should().BeFalse();
        tracker.TryMarkWarned("claude").Should().BeTrue();
    }

    [Fact]
    public void TryMarkWarned_IsCaseInsensitive()
    {
        var tracker = new BillingUnpricedModelTracker();

        tracker.TryMarkWarned("GPT-4o").Should().BeTrue();
        tracker.TryMarkWarned("gpt-4o").Should().BeFalse();
    }

    [Fact]
    public void Clear_AllowsModelToWarnAgain()
    {
        var tracker = new BillingUnpricedModelTracker();

        tracker.TryMarkWarned("gpt-4o").Should().BeTrue();
        tracker.Clear("gpt-4o");

        tracker.TryMarkWarned("gpt-4o").Should().BeTrue();
    }

    [Fact]
    public void RetentionLimit_BoundsMemory()
    {
        var tracker = new BillingUnpricedModelTracker(retentionLimit: 2);

        tracker.TryMarkWarned("a").Should().BeTrue();
        tracker.TryMarkWarned("b").Should().BeTrue();
        tracker.TryMarkWarned("c").Should().BeTrue();

        // "a" was evicted as the oldest, so it may warn again.
        tracker.TryMarkWarned("a").Should().BeTrue();
        tracker.TryMarkWarned("c").Should().BeFalse();
    }
}
