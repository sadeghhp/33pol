using Pol33.Billing.Usage;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingBudgetWarningTrackerTests
{
    [Fact]
    public void TryMarkSent_FirstKey_ReturnsTrue()
    {
        var tracker = new BillingBudgetWarningTracker();

        tracker.TryMarkSent("tenant:budget:2026-05-01").Should().BeTrue();
    }

    [Fact]
    public void TryMarkSent_DuplicateKey_ReturnsFalse()
    {
        var tracker = new BillingBudgetWarningTracker();
        var key = "tenant:budget:2026-05-01";

        tracker.TryMarkSent(key).Should().BeTrue();
        tracker.TryMarkSent(key).Should().BeFalse();
    }

    [Fact]
    public void TryMarkSent_WhenRetentionLimitExceeded_EvictsOldestKeys()
    {
        var tracker = new BillingBudgetWarningTracker(retentionLimit: 1);

        tracker.TryMarkSent("k1").Should().BeTrue();
        tracker.TryMarkSent("k2").Should().BeTrue();
        tracker.TryMarkSent("k1").Should().BeTrue();
    }

    [Fact]
    public void TryMarkSent_WhenRetentionLimitIsInvalid_UsesMinimumRetention()
    {
        var tracker = new BillingBudgetWarningTracker(retentionLimit: 0);

        tracker.TryMarkSent("k1").Should().BeTrue();
        tracker.TryMarkSent("k2").Should().BeTrue();
        tracker.TryMarkSent("k1").Should().BeTrue();
    }
}
