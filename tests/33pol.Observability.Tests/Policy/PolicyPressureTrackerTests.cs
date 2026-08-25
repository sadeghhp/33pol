using Pol33.Core.Models.Overview;
using Pol33.Observability.Policy;

namespace Pol33.Observability.Tests.Policy;

public sealed class PolicyPressureTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_RanksReasonsTenantsAndModels()
    {
        var clock = new FakeTimeProvider(Start);
        var tracker = new PolicyPressureTracker(clock);

        tracker.RecordRejection(RejectionReason.RateLimit, "tenant-a", null);
        tracker.RecordRejection(RejectionReason.RateLimit, "tenant-a", null);
        tracker.RecordRejection(RejectionReason.Quota, "tenant-b", "m1");
        tracker.RecordGrantDenial("tenant-b", "m2");
        tracker.RecordBudgetRejection("tenant-a", "R&D", "m1");
        tracker.RecordUnknownModel("gpt-5-ultra");

        var s = tracker.Snapshot();

        s.RejectionsByReason1h.Select(r => (r.Key, r.Count)).Should().Equal(
            ("rate_limit", 2L), ("budget", 1L), ("grant_denied", 1L), ("model_not_found", 1L), ("quota", 1L));
        s.RejectionsByTenant1h[0].Should().Be(new CountRow("tenant-a", 3));
        s.RejectionsByModel1h.Should().ContainSingle(r => r.Key == "m1" && r.Count == 2);
        s.UnknownModels1h.Should().ContainSingle(r => r.Key == "gpt-5-ultra");
        s.GrantDenials1h.Should().ContainSingle(r => r.Key == "tenant-b|m2");
        s.BudgetRejections1h.Should().ContainSingle(r => r.Key == "R&D");
    }

    [Fact]
    public void Snapshot_WindowsExpireOldEntries()
    {
        var clock = new FakeTimeProvider(Start);
        var tracker = new PolicyPressureTracker(clock);

        tracker.RecordRejection(RejectionReason.RateLimit, "t", null);
        clock.Advance(TimeSpan.FromMinutes(90));
        tracker.RecordRejection(RejectionReason.Quota, "t", null);

        var s = tracker.Snapshot();
        s.RejectionsByReason1h.Should().ContainSingle(r => r.Key == "quota");
        s.RejectionsByReason24h.Should().HaveCount(2);

        clock.Advance(TimeSpan.FromHours(24));
        tracker.Snapshot().RejectionsByReason24h.Should().BeEmpty();
    }

    [Fact]
    public void Record_PastTheKeyCap_IgnoresNewKeys()
    {
        var tracker = new PolicyPressureTracker(new FakeTimeProvider(Start));
        for (var i = 0; i < PolicyPressureTracker.MaxKeysPerDimension + 5; i++)
        {
            tracker.RecordRejection(RejectionReason.RateLimit, "tenant-" + i, null);
        }

        tracker.Snapshot(take: 1000).RejectionsByTenant1h.Should().HaveCount(PolicyPressureTracker.MaxKeysPerDimension);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
