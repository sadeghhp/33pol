using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Policy.Quotas;

namespace Pol33.Policy.Tests.Quotas;

public sealed class InMemoryQuotaServiceTests
{
    [Fact]
    public void CheckBeforeForward_WhenUnderLimit_ReturnsAllowed()
    {
        var service = CreateService(limit: 1000);
        service.CheckBeforeForward("t1", "model").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void CheckBeforeForward_WhenAtHardLimit_ReturnsBlocked()
    {
        var service = CreateService(limit: 10);
        service.CommitUsage("t1", "m", 10, "req-1");
        service.CheckBeforeForward("t1", "m").IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void CommitUsage_SameRequestId_IsIdempotent()
    {
        var service = CreateService(limit: 100);
        service.CommitUsage("t1", "m", 5, "req-1");
        service.CommitUsage("t1", "m", 5, "req-1");
        service.CheckBeforeForward("t1", "m").IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void CommitUsage_WhenRetentionWindowExceeded_EvictsOldestRequestIds()
    {
        var service = CreateService(limit: 10, committedRequestRetentionLimit: 1);

        service.CommitUsage("t1", "m", 5, "req-1");
        service.CommitUsage("t1", "m", 5, "req-2");

        // req-1 falls out of retention window and can be counted again.
        service.CommitUsage("t1", "m", 5, "req-1");

        service.CheckBeforeForward("t1", "m").IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void CommitUsage_WhenRetentionLimitIsInvalid_UsesMinimumWindow()
    {
        var service = CreateService(limit: 10, committedRequestRetentionLimit: 0);

        service.CommitUsage("t1", "m", 5, "req-1");
        service.CommitUsage("t1", "m", 5, "req-2");
        service.CommitUsage("t1", "m", 5, "req-1");

        service.CheckBeforeForward("t1", "m").IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void CheckBeforeForward_AfterMonthRollover_ResetsUsage()
    {
        var now = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(limit: 10, clock: () => now);

        service.CommitUsage("t1", "m", 10, "req-jan");
        service.CheckBeforeForward("t1", "m").IsAllowed.Should().BeFalse(); // January exhausted

        now = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero); // UTC month rollover

        // A "monthly" limit must reset at the month boundary rather than blocking forever.
        service.CheckBeforeForward("t1", "m").IsAllowed.Should().BeTrue();
    }

    /// <summary>
    /// Stale partitions from a closed month are dropped once the month rolls over — but the
    /// eviction scan is not paid on every commit (it used to enumerate every partition per event).
    /// </summary>
    [Fact]
    public void CommitUsage_AfterMonthRollover_EvictsPartitionsFromTheClosedPeriod()
    {
        var now = new DateTimeOffset(2026, 1, 31, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(limit: 1000, clock: () => now);

        service.CommitUsage("t-jan-only", "m", 10, "req-a");
        service.CommitUsage("t-both", "m", 10, "req-b");
        service.ExportUsage().Should().HaveCount(2);

        now = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        service.CommitUsage("t-both", "m", 5, "req-c");

        var exported = service.ExportUsage();
        exported.Should().ContainSingle(u => u.PartitionKey == "t-both" && u.Period == "2026-02" && u.Used == 5);
        exported.Should().NotContain(u => u.PartitionKey == "t-jan-only");
    }

    private static InMemoryQuotaService CreateService(
        long limit,
        int committedRequestRetentionLimit = 100_000,
        Func<DateTimeOffset>? clock = null)
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();

        // The monthly limit and soft ratio now come from the config snapshot; only the retention
        // limit stays on QuotaOptions.
        var provider = new StubConfigProvider(new GatewayConfigSnapshot
        {
            Quota = new QuotaConfigSection { DefaultMonthlyTokenLimit = limit, SoftLimitRatio = 0.9 },
        });
        var options = Options.Create(new QuotaOptions
        {
            CommittedRequestIdRetentionLimit = committedRequestRetentionLimit,
        });
        return new InMemoryQuotaService(provider, options, metrics, clock);
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current => snapshot;
    }

    [Fact]
    public void CheckBeforeForward_WhenAtHardLimit_ReportsThePartitionAndModel()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var provider = new StubConfigProvider(new GatewayConfigSnapshot
        {
            Quota = new QuotaConfigSection { DefaultMonthlyTokenLimit = 10, SoftLimitRatio = 0.9 },
        });
        var service = new InMemoryQuotaService(provider, Options.Create(new QuotaOptions()), metrics);
        service.CommitUsage("t1", "m", 10, "req-1");

        service.CheckBeforeForward("t1", "m").IsAllowed.Should().BeFalse();

        metrics.Received(1).RecordQuotaRejection("t1", "m");
    }

}
