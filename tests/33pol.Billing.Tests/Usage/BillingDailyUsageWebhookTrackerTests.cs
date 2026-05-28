using Pol33.Billing.Usage;

namespace Pol33.Billing.Tests.Usage;

public sealed class BillingDailyUsageWebhookTrackerTests
{
    [Fact]
    public void TryMarkSent_FirstTenantDay_ReturnsTrue()
    {
        var tracker = new BillingDailyUsageWebhookTracker();
        tracker.TryMarkSent(Guid.NewGuid(), new DateOnly(2026, 5, 26)).Should().BeTrue();
    }

    [Fact]
    public void TryMarkSent_DuplicateTenantDay_ReturnsFalse()
    {
        var tracker = new BillingDailyUsageWebhookTracker();
        var tenantId = Guid.NewGuid();
        var day = new DateOnly(2026, 5, 26);

        tracker.TryMarkSent(tenantId, day).Should().BeTrue();
        tracker.TryMarkSent(tenantId, day).Should().BeFalse();
    }

    [Fact]
    public void TryMarkSent_WhenRetentionLimitExceeded_EvictsOldestTenantDay()
    {
        var tracker = new BillingDailyUsageWebhookTracker(retentionLimit: 1);
        var tenantId = Guid.NewGuid();
        var day1 = new DateOnly(2026, 5, 26);
        var day2 = day1.AddDays(1);

        tracker.TryMarkSent(tenantId, day1).Should().BeTrue();
        tracker.TryMarkSent(tenantId, day2).Should().BeTrue();
        tracker.TryMarkSent(tenantId, day1).Should().BeTrue();
    }

    [Fact]
    public void TryMarkSent_WhenRetentionLimitIsInvalid_UsesMinimumRetention()
    {
        var tracker = new BillingDailyUsageWebhookTracker(retentionLimit: -1);
        var tenantId = Guid.NewGuid();
        var day1 = new DateOnly(2026, 5, 26);
        var day2 = day1.AddDays(1);

        tracker.TryMarkSent(tenantId, day1).Should().BeTrue();
        tracker.TryMarkSent(tenantId, day2).Should().BeTrue();
        tracker.TryMarkSent(tenantId, day1).Should().BeTrue();
    }
}
