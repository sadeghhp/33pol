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

    private static InMemoryQuotaService CreateService(long limit)
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var options = Options.Create(new QuotaOptions
        {
            DefaultMonthlyTokenLimit = limit,
            SoftLimitRatio = 0.9,
        });
        return new InMemoryQuotaService(options, metrics);
    }
}
