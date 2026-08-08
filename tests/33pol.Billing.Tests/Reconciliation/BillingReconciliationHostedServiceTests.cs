using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pol33.Billing.Reconciliation;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Tests.Reconciliation;

public sealed class BillingReconciliationHostedServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 15, 4, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The window ends at yesterday, never today: today's rollups are still being written, so
    /// comparing a day in progress races the usage writer's flush and reports drift that resolves
    /// itself seconds later — noise that would train operators to ignore the alert.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_ReconcilesTheLookbackWindowEndingYesterday()
    {
        var reconciler = CreateReconciler();
        var service = CreateService(reconciler, options => options.ReconciliationLookbackDays = 3);

        await service.RunOnceAsync(Now, CancellationToken.None);

        await reconciler.Received(1).ReconcileAsync(
            new DateOnly(2026, 3, 12),
            new DateOnly(2026, 3, 14),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_WithASingleDayLookback_ReconcilesYesterdayOnly()
    {
        var reconciler = CreateReconciler();
        var service = CreateService(reconciler, options => options.ReconciliationLookbackDays = 1);

        await service.RunOnceAsync(Now, CancellationToken.None);

        await reconciler.Received(1).ReconcileAsync(
            new DateOnly(2026, 3, 14),
            new DateOnly(2026, 3, 14),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Retention prunes the ledger but not the rollups, so a window reaching past it would report
    /// every pruned day as a discrepancy — a true statement about the data that says nothing about
    /// correctness, and enough of them to bury a real finding.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_ClampsTheWindowInsideTheRetentionPeriod()
    {
        var reconciler = CreateReconciler();
        var service = CreateService(reconciler, options =>
        {
            options.ReconciliationLookbackDays = 365;
            options.UsageRetentionDays = 7;
        });

        await service.RunOnceAsync(Now, CancellationToken.None);

        // 6 days back from yesterday: the window stays strictly inside the 7-day retention.
        await reconciler.Received(1).ReconcileAsync(
            new DateOnly(2026, 3, 9),
            new DateOnly(2026, 3, 14),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task RunOnceAsync_WithANonPositiveLookback_StillReconcilesYesterday(int lookbackDays)
    {
        var reconciler = CreateReconciler();
        var service = CreateService(reconciler, options => options.ReconciliationLookbackDays = lookbackDays);

        await service.RunOnceAsync(Now, CancellationToken.None);

        await reconciler.Received(1).ReconcileAsync(
            new DateOnly(2026, 3, 14),
            new DateOnly(2026, 3, 14),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A misconfigured retention must not collapse the window to nothing and silently stop checking.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_WithDegenerateRetention_StillReconcilesYesterday()
    {
        var reconciler = CreateReconciler();
        var service = CreateService(reconciler, options =>
        {
            options.ReconciliationLookbackDays = 30;
            options.UsageRetentionDays = 0;
        });

        await service.RunOnceAsync(Now, CancellationToken.None);

        await reconciler.Received(1).ReconcileAsync(
            new DateOnly(2026, 3, 14),
            new DateOnly(2026, 3, 14),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunOnceAsync_WhenBalanced_ReportsZeroRatherThanNotReporting()
    {
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var service = CreateService(CreateReconciler(), metrics: metrics);

        await service.RunOnceAsync(Now, CancellationToken.None);

        // A balanced sweep must still publish. Reporting only on failure makes "healthy" and "the job
        // died" indistinguishable, which is the failure mode this whole feature exists to remove.
        metrics.Received(1).RecordBillingReconciliation(0, 0d);
    }

    [Fact]
    public async Task RunOnceAsync_WhenDiscrepanciesExist_PublishesCountAndAbsoluteDrift()
    {
        var key = new DailyUsageRollupKey(new DateOnly(2026, 3, 14), Guid.NewGuid(), "gpt-4o", null);
        var report = new BillingReconciliationReport(
            new DateOnly(2026, 3, 12),
            new DateOnly(2026, 3, 14),
            5,
            new BillingReconciliationTotals(100, 50, 9.99m, 3),
            BillingReconciliationTotals.Zero,
            [
                new BillingReconciliationDiscrepancy(
                    BillingReconciliationKind.MissingFromRollups,
                    key,
                    new BillingReconciliationTotals(100, 50, 9.99m, 3),
                    BillingReconciliationTotals.Zero),
            ]);

        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var service = CreateService(CreateReconciler(report), metrics: metrics);

        await service.RunOnceAsync(Now, CancellationToken.None);

        // Absolute, not net: offsetting drift between buckets must not cancel out into a clean bill.
        metrics.Received(1).RecordBillingReconciliation(1, 9.99d);
    }

    /// <summary>
    /// The metrics collector lives in a module billing does not reference. Requiring it in the
    /// constructor made every composition without observability throw at startup.
    /// </summary>
    [Fact]
    public async Task RunOnceAsync_WithNoMetricsCollectorRegistered_StillReconciles()
    {
        var reconciler = CreateReconciler();
        var service = CreateService(reconciler, metrics: null);

        await service.RunOnceAsync(Now, CancellationToken.None);

        await reconciler.Received(1).ReconcileAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    private static IBillingReconciliationService CreateReconciler(BillingReconciliationReport? report = null)
    {
        var reconciler = Substitute.For<IBillingReconciliationService>();
        reconciler
            .ReconcileAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(
                report ?? BillingReconciliationReport.Empty(new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 14))));
        return reconciler;
    }

    private static BillingReconciliationHostedService CreateService(
        IBillingReconciliationService reconciler,
        Action<BillingOptions>? configure = null,
        IGatewayMetricsCollector? metrics = null)
    {
        var options = new BillingOptions();
        configure?.Invoke(options);

        var services = new ServiceCollection();
        services.AddScoped(_ => reconciler);
        if (metrics is not null)
        {
            services.AddSingleton(metrics);
        }

        return new BillingReconciliationHostedService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<BillingReconciliationHostedService>.Instance);
    }
}
