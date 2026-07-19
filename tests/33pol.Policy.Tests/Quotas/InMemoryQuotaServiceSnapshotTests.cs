using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Policy.Quotas;

namespace Pol33.Policy.Tests.Quotas;

public sealed class InMemoryQuotaServiceSnapshotTests
{
    private static InMemoryQuotaService CreateService(long limit, Func<DateTimeOffset> clock)
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var provider = new StubConfigProvider(new GatewayConfigSnapshot
        {
            Quota = new QuotaConfigSection { DefaultMonthlyTokenLimit = limit, SoftLimitRatio = 0.9 },
        });
        var options = Options.Create(new QuotaOptions
        {
            CommittedRequestIdRetentionLimit = 100_000,
        });
        return new InMemoryQuotaService(provider, options, metrics, clock);
    }

    private sealed class StubConfigProvider(GatewayConfigSnapshot snapshot) : IGatewayConfigProvider
    {
        public GatewayConfigSnapshot Current => snapshot;
    }

    [Fact]
    public void ExportUsage_ReflectsCommittedUsageForCurrentPeriod()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(limit: 1000, () => now);
        service.CommitUsage("key-a", "m", 300, "req-1");
        service.CommitUsage("key-b", "m", 50, "req-2");

        var exported = service.ExportUsage();

        exported.Should().ContainSingle(u => u.PartitionKey == "key-a" && u.Used == 300 && u.Period == "2026-07");
        exported.Should().ContainSingle(u => u.PartitionKey == "key-b" && u.Used == 50);
    }

    [Fact]
    public void HydrateUsage_RestoresUsageSoLimitStaysEnforcedAfterRestart()
    {
        var now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var restarted = CreateService(limit: 100, () => now);

        // Simulate a container recreation: fresh service, hydrate persisted usage.
        restarted.HydrateUsage([new QuotaUsageSnapshot("key-a", "2026-07", 100)]);

        restarted.CheckBeforeForward("key-a", "m").IsAllowed.Should().BeFalse();
    }

    [Fact]
    public void HydrateUsage_DropsStalePeriodSoNewMonthStartsFresh()
    {
        var now = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var service = CreateService(limit: 100, () => now);

        // Persisted usage is from the previous month; it must not suppress July's fresh allowance.
        service.HydrateUsage([new QuotaUsageSnapshot("key-a", "2026-06", 100)]);

        service.CheckBeforeForward("key-a", "m").IsAllowed.Should().BeTrue();
        service.ExportUsage().Should().BeEmpty();
    }
}
