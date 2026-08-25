using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Billing.Reconciliation;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Tests.Reconciliation;

public sealed class BillingReconciliationStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Current_BeforeAnySweep_IsNotEnabledAndHasNoRun()
    {
        var state = new BillingReconciliationState();

        state.Current.Enabled.Should().BeFalse();
        state.Current.LastRunUtc.Should().BeNull();
        state.Current.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public void Record_CapturesTheReport()
    {
        var state = new BillingReconciliationState();
        var report = BillingReconciliationReport.Empty(new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 14));

        state.Record(report, Now);

        state.Current.Enabled.Should().BeTrue();
        state.Current.LastRunUtc.Should().Be(Now);
        state.Current.FromDate.Should().Be(new DateOnly(2026, 3, 12));
        state.Current.ToDate.Should().Be(new DateOnly(2026, 3, 14));
        state.Current.DiscrepancyCount.Should().Be(0);
    }

    [Fact]
    public async Task RunOnceAsync_RecordsTheSweepOnTheState()
    {
        var reconciler = Substitute.For<IBillingReconciliationService>();
        reconciler
            .ReconcileAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BillingReconciliationReport.Empty(new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 14))));
        var services = new ServiceCollection();
        services.AddScoped(_ => reconciler);
        var state = new BillingReconciliationState();
        var service = new BillingReconciliationHostedService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BillingOptions()),
            NullLogger<BillingReconciliationHostedService>.Instance,
            state);

        await service.RunOnceAsync(Now, CancellationToken.None);

        state.Current.LastRunUtc.Should().Be(Now);
        state.Current.Enabled.Should().BeTrue();
    }
}
