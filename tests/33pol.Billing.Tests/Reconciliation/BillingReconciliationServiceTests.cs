using NSubstitute;
using Pol33.Billing.Reconciliation;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;

namespace Pol33.Billing.Tests.Reconciliation;

public sealed class BillingReconciliationServiceTests
{
    private static readonly DateOnly Day = new(2026, 3, 14);
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ReconcileAsync_WhenRollupsMatchTheLedger_ReportsBalanced()
    {
        var bucket = Rollup(promptTokens: 100, completionTokens: 50, cost: 1.25m, requests: 3);
        var service = CreateService(ledger: [bucket], rollups: [bucket]);

        var report = await service.ReconcileAsync(Day, Day);

        report.IsBalanced.Should().BeTrue();
        report.BucketsCompared.Should().Be(1);
        report.AbsoluteCostDrift.Should().Be(0m);
        report.EventTotals.TotalCost.Should().Be(1.25m);
        report.RollupTotals.TotalCost.Should().Be(1.25m);
    }

    /// <summary>
    /// The failure this whole job exists to catch: the ledger recorded the spend, the rollup write
    /// that follows it did not land, and nothing errored.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_WhenARollupIsMissing_ReportsMissingFromRollups()
    {
        var service = CreateService(
            ledger: [Rollup(promptTokens: 100, completionTokens: 50, cost: 1.25m, requests: 3)],
            rollups: []);

        var report = await service.ReconcileAsync(Day, Day);

        var discrepancy = report.Discrepancies.Should().ContainSingle().Subject;
        discrepancy.Kind.Should().Be(BillingReconciliationKind.MissingFromRollups);
        discrepancy.Events.TotalCost.Should().Be(1.25m);
        discrepancy.Rollup.Should().Be(BillingReconciliationTotals.Zero);

        // Rollups under-report the ledger, so the drift is negative — the direction operators need,
        // because under-reporting means unbilled spend while over-reporting means overcharging.
        discrepancy.CostDelta.Should().Be(-1.25m);
        report.NetCostDrift.Should().Be(-1.25m);
        report.AbsoluteCostDrift.Should().Be(1.25m);
    }

    [Fact]
    public async Task ReconcileAsync_WhenARollupHasNoLedgerBehindIt_ReportsMissingFromEvents()
    {
        var service = CreateService(
            ledger: [],
            rollups: [Rollup(promptTokens: 10, completionTokens: 5, cost: 0.5m, requests: 1)]);

        var report = await service.ReconcileAsync(Day, Day);

        var discrepancy = report.Discrepancies.Should().ContainSingle().Subject;
        discrepancy.Kind.Should().Be(BillingReconciliationKind.MissingFromEvents);
        discrepancy.CostDelta.Should().Be(0.5m);
        report.RollupTotals.TotalCost.Should().Be(0.5m);
        report.EventTotals.TotalCost.Should().Be(0m);
    }

    [Fact]
    public async Task ReconcileAsync_WhenTotalsDiffer_ReportsTheDeltaInBothDirections()
    {
        var service = CreateService(
            ledger: [Rollup(promptTokens: 100, completionTokens: 50, cost: 2.00m, requests: 4)],
            rollups: [Rollup(promptTokens: 90, completionTokens: 50, cost: 1.75m, requests: 3)]);

        var report = await service.ReconcileAsync(Day, Day);

        var discrepancy = report.Discrepancies.Should().ContainSingle().Subject;
        discrepancy.Kind.Should().Be(BillingReconciliationKind.TotalsDiffer);
        discrepancy.CostDelta.Should().Be(-0.25m);
        discrepancy.TokenDelta.Should().Be(-10);
        discrepancy.RequestCountDelta.Should().Be(-1);
    }

    /// <summary>
    /// A token or request-count difference at identical cost is still a defect — it means usage was
    /// mis-attributed even though the money happened to agree.
    /// </summary>
    [Theory]
    [InlineData(90, 50, 3)]
    [InlineData(100, 40, 3)]
    [InlineData(100, 50, 2)]
    public async Task ReconcileAsync_WhenOnlyNonCostTotalsDiffer_StillReportsADiscrepancy(
        long promptTokens,
        long completionTokens,
        int requests)
    {
        var service = CreateService(
            ledger: [Rollup(promptTokens: 100, completionTokens: 50, cost: 1m, requests: 3)],
            rollups: [Rollup(promptTokens, completionTokens, cost: 1m, requests)]);

        var report = await service.ReconcileAsync(Day, Day);

        report.Discrepancies.Should().ContainSingle()
            .Which.Kind.Should().Be(BillingReconciliationKind.TotalsDiffer);
        report.AbsoluteCostDrift.Should().Be(0m);
    }

    /// <summary>
    /// Guards the exact-comparison decision: a tolerance wide enough to absorb this would also
    /// absorb a genuinely dropped small request.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_WhenCostDiffersInTheLastDecimalPlace_ReportsADiscrepancy()
    {
        var service = CreateService(
            ledger: [Rollup(promptTokens: 1, completionTokens: 1, cost: 0.0000000001m, requests: 1)],
            rollups: [Rollup(promptTokens: 1, completionTokens: 1, cost: 0.0000000002m, requests: 1)]);

        var report = await service.ReconcileAsync(Day, Day);

        report.IsBalanced.Should().BeFalse();
        report.AbsoluteCostDrift.Should().Be(0.0000000001m);
    }

    /// <summary>
    /// Offsetting drift in two buckets nets to zero. The absolute figure is what alerting must use,
    /// which is the reason both are reported.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_WhenDriftOffsetsBetweenBuckets_NetIsZeroButAbsoluteIsNot()
    {
        var service = CreateService(
            ledger:
            [
                Rollup(promptTokens: 10, completionTokens: 0, cost: 5m, requests: 1, modelId: "model-a"),
                Rollup(promptTokens: 10, completionTokens: 0, cost: 5m, requests: 1, modelId: "model-b"),
            ],
            rollups:
            [
                Rollup(promptTokens: 10, completionTokens: 0, cost: 4m, requests: 1, modelId: "model-a"),
                Rollup(promptTokens: 10, completionTokens: 0, cost: 6m, requests: 1, modelId: "model-b"),
            ]);

        var report = await service.ReconcileAsync(Day, Day);

        report.Discrepancies.Should().HaveCount(2);
        report.NetCostDrift.Should().Be(0m);
        report.AbsoluteCostDrift.Should().Be(2m);
    }

    [Fact]
    public async Task ReconcileAsync_OrdersDiscrepanciesByLargestAbsoluteCostDelta()
    {
        var service = CreateService(
            ledger:
            [
                Rollup(promptTokens: 1, completionTokens: 0, cost: 1m, requests: 1, modelId: "small"),
                Rollup(promptTokens: 1, completionTokens: 0, cost: 100m, requests: 1, modelId: "large"),
            ],
            rollups: []);

        var report = await service.ReconcileAsync(Day, Day);

        report.Discrepancies.Select(d => d.Key.ModelId).Should().Equal("large", "small");
    }

    /// <summary>
    /// Buckets are keyed on the normalised cost centre, exactly as the rollup writer keys them. A
    /// null on one side and blank on the other is the same bucket, not two.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_TreatsBlankAndNullCostCentreAsTheSameBucket()
    {
        var service = CreateService(
            ledger: [Rollup(promptTokens: 10, completionTokens: 0, cost: 1m, requests: 1, costCenter: null)],
            rollups: [Rollup(promptTokens: 10, completionTokens: 0, cost: 1m, requests: 1, costCenter: "  ")]);

        var report = await service.ReconcileAsync(Day, Day);

        report.IsBalanced.Should().BeTrue();
        report.BucketsCompared.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileAsync_DistinguishesTenantsWithinTheSameModelAndDay()
    {
        var otherTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var service = CreateService(
            ledger:
            [
                Rollup(promptTokens: 10, completionTokens: 0, cost: 1m, requests: 1),
                Rollup(promptTokens: 20, completionTokens: 0, cost: 2m, requests: 1, tenantId: otherTenant),
            ],
            rollups:
            [
                Rollup(promptTokens: 10, completionTokens: 0, cost: 1m, requests: 1),
                Rollup(promptTokens: 20, completionTokens: 0, cost: 2m, requests: 1, tenantId: otherTenant),
            ]);

        var report = await service.ReconcileAsync(Day, Day);

        report.BucketsCompared.Should().Be(2);
        report.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task ReconcileAsync_WhenWindowIsInverted_ReturnsAnEmptyReportWithoutQuerying()
    {
        var events = Substitute.For<IBillingEventRepository>();
        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        var service = new BillingReconciliationService(events, rollups);

        var report = await service.ReconcileAsync(Day, Day.AddDays(-1));

        report.IsBalanced.Should().BeTrue();
        report.BucketsCompared.Should().Be(0);
        await events.DidNotReceiveWithAnyArgs().GetDailyTotalsAsync(default, default);
        await rollups.DidNotReceiveWithAnyArgs().GetRollupsAsync(default, default, default);
    }

    [Fact]
    public async Task ReconcileAsync_ReadsRollupsAcrossEveryTenant()
    {
        var events = Substitute.For<IBillingEventRepository>();
        events.GetDailyTotalsAsync(Day, Day, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DailyUsageRollupRecord>>([]));
        var rollups = Substitute.For<IDailyUsageRollupRepository>();
        rollups.GetRollupsAsync(Day, Day, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DailyUsageRollupRecord>>([]));

        await new BillingReconciliationService(events, rollups).ReconcileAsync(Day, Day);

        // A tenant filter here would silently reconcile one tenant and declare the gateway balanced.
        await rollups.Received(1).GetRollupsAsync(Day, Day, null, Arg.Any<CancellationToken>());
    }

    private static BillingReconciliationService CreateService(
        IReadOnlyList<DailyUsageRollupRecord> ledger,
        IReadOnlyList<DailyUsageRollupRecord> rollups)
    {
        var events = Substitute.For<IBillingEventRepository>();
        events.GetDailyTotalsAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ledger));

        var rollupRepository = Substitute.For<IDailyUsageRollupRepository>();
        rollupRepository
            .GetRollupsAsync(Arg.Any<DateOnly?>(), Arg.Any<DateOnly?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rollups));

        return new BillingReconciliationService(events, rollupRepository);
    }

    private static DailyUsageRollupRecord Rollup(
        long promptTokens,
        long completionTokens,
        decimal cost,
        int requests,
        string modelId = "gpt-4o",
        string? costCenter = null,
        Guid? tenantId = null) =>
        new(Day, tenantId ?? Tenant, modelId, costCenter, promptTokens, completionTokens, cost, requests);
}
