using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pol33.Billing.Reconciliation;
using Pol33.Core.Billing;
using Pol33.Persistence;
using Pol33.Persistence.Infrastructure;
using Pol33.Persistence.Repositories;

namespace Pol33.Billing.Tests.Reconciliation;

/// <summary>
/// Reconciliation driven by the real ledger and rollup repositories on a real SQLite engine.
/// </summary>
/// <remarks>
/// <para>The service's own unit tests use stubbed repositories, so they prove the comparison logic
/// but not the thing that decides whether this job is usable: that the ledger aggregation and the
/// rollup writer bucket usage <em>identically</em>. If they disagree by so much as a cost-centre
/// normalisation, every sweep reports discrepancies that are artefacts of the comparison, operators
/// learn to ignore the alert, and the one real defect it was built to catch arrives unnoticed.</para>
///
/// <para>So the balanced cases here matter as much as the detection cases — arguably more.</para>
/// </remarks>
public sealed class SqliteBillingReconciliationTests
{
    private static readonly DateOnly Day = new(2026, 3, 14);
    private static readonly DateTimeOffset Noon = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A real SQLite engine, configured exactly as production is. The in-memory EF provider keeps
    /// decimals as CLR decimals and would hide the TEXT round-trip these totals depend on.
    /// </summary>
    private static GatewayDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<GatewayDbContext>();
        SqliteGatewayDbContext.Configure(options, connectionString);
        return new GatewayDbContext(options.Options);
    }

    private static string NewSharedInMemoryConnectionString()
        => $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    private static async Task<SqliteConnection> MigratedKeepAliveAsync(string connectionString)
    {
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();

        await using var db = CreateDb(connectionString);
        await db.Database.MigrateAsync();
        return keepAlive;
    }

    [Fact]
    public async Task Reconcile_WhenUsageTookTheNormalPath_ReportsBalanced()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = CreateDb(connectionString);

        var tenant = Guid.NewGuid();
        var events = new[]
        {
            Event("n1", tenant, "gpt-4o", "eng", 10, 5, 0.10m, Noon),
            Event("n2", tenant, "gpt-4o", "eng", 20, 10, 0.20m, Noon.AddHours(2)),
            Event("n3", tenant, "gpt-4o", null, 5, 1, 0.05m, Noon),
            Event("n4", tenant, "claude", "eng", 7, 2, 0.0000000007m, Noon),
            Event("n5", Guid.NewGuid(), "gpt-4o", "eng", 3, 1, 0.03m, Noon),
        };

        var report = await PersistAndReconcileAsync(db, events);

        report.IsBalanced.Should().BeTrue(
            "the ledger aggregation and the rollup writer must bucket usage identically");
        report.BucketsCompared.Should().Be(4);
        report.EventTotals.TotalCost.Should().Be(report.RollupTotals.TotalCost);
    }

    /// <summary>
    /// Usage arriving in several flushes must reconcile the same as one — the rollup writer applies
    /// additive increments, and the ledger side sums the whole day at once.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenUsageArrivedAcrossSeveralFlushes_ReportsBalanced()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = CreateDb(connectionString);
        var tenant = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            await PersistAsync(db, [Event($"batch-{i}", tenant, "gpt-4o", "eng", 10, 5, 0.11m, Noon)]);
        }

        var report = await ReconcileAsync(db);

        report.IsBalanced.Should().BeTrue();
        report.EventTotals.RequestCount.Should().Be(5);
        report.EventTotals.TotalCost.Should().Be(0.55m);
    }

    /// <summary>
    /// The exact failure this job exists for: the ledger append succeeded and the rollup increment
    /// did not. Nothing else in the gateway notices — the request was served and billed.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenARollupIncrementNeverLanded_DetectsTheUnrolledSpend()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = CreateDb(connectionString);
        var tenant = Guid.NewGuid();

        await PersistAsync(db, [Event("kept", tenant, "gpt-4o", "eng", 10, 5, 0.10m, Noon)]);

        // Ledger only — exactly what a rollup write that threw after the append leaves behind.
        var eventRepository = new BillingEventRepository(db);
        await eventRepository.TryAppendAsync(Event("dropped", tenant, "claude", "eng", 99, 99, 9.99m, Noon));

        var report = await ReconcileAsync(db);

        var discrepancy = report.Discrepancies.Should().ContainSingle().Subject;
        discrepancy.Kind.Should().Be(BillingReconciliationKind.MissingFromRollups);
        discrepancy.Key.ModelId.Should().Be("claude");
        discrepancy.CostDelta.Should().Be(-9.99m);
        report.AbsoluteCostDrift.Should().Be(9.99m);
    }

    /// <summary>
    /// A rollup incremented twice for the same usage — over-reporting, which charges a tenant for
    /// spend that never happened and can trip a hard-stop budget on phantom cost.
    /// </summary>
    [Fact]
    public async Task Reconcile_WhenARollupWasIncrementedTwice_DetectsTheOverCount()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = CreateDb(connectionString);
        var tenant = Guid.NewGuid();
        var events = new[] { Event("dup", tenant, "gpt-4o", "eng", 10, 5, 0.10m, Noon) };

        await PersistAsync(db, events);
        await new DailyUsageRollupRepository(db).IncrementRollupsAsync(ToDeltas(events));

        var report = await ReconcileAsync(db);

        var discrepancy = report.Discrepancies.Should().ContainSingle().Subject;
        discrepancy.Kind.Should().Be(BillingReconciliationKind.TotalsDiffer);
        discrepancy.CostDelta.Should().Be(0.10m, "the rollup now holds twice the ledger's cost");
        discrepancy.RequestCountDelta.Should().Be(1);
    }

    /// <summary>
    /// Tiny per-request costs are where a floating-point sum on either side would show up first, and
    /// where a coarser rounding scale would collapse the total to zero.
    /// </summary>
    [Fact]
    public async Task Reconcile_WithCostsAtTheStorageScale_ReportsBalanced()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = CreateDb(connectionString);
        var tenant = Guid.NewGuid();

        var events = Enumerable.Range(0, 20)
            .Select(i => Event($"tiny-{i}", tenant, "tiny", null, 1, 1, 0.0000000003m, Noon))
            .ToArray();

        var report = await PersistAndReconcileAsync(db, events);

        report.IsBalanced.Should().BeTrue();
        report.EventTotals.TotalCost.Should().Be(0.0000000060m);
    }

    [Fact]
    public async Task Reconcile_IgnoresUsageOutsideTheWindow()
    {
        var connectionString = NewSharedInMemoryConnectionString();
        await using var keepAlive = await MigratedKeepAliveAsync(connectionString);
        await using var db = CreateDb(connectionString);
        var tenant = Guid.NewGuid();

        await PersistAsync(db, [Event("in", tenant, "gpt-4o", null, 1, 1, 0.01m, Noon)]);

        // Ledger-only spend on a different day must not surface when that day is not reconciled.
        await new BillingEventRepository(db)
            .TryAppendAsync(Event("out", tenant, "gpt-4o", null, 1, 1, 5m, Noon.AddDays(-4)));

        var report = await ReconcileAsync(db);

        report.IsBalanced.Should().BeTrue();
        report.BucketsCompared.Should().Be(1);
    }

    private static async Task<BillingReconciliationReport> PersistAndReconcileAsync(
        GatewayDbContext db,
        IReadOnlyList<BillingEventRecord> events)
    {
        await PersistAsync(db, events);
        return await ReconcileAsync(db);
    }

    /// <summary>
    /// Mirrors what <c>BillingUsagePersistenceHandler</c> does: append each event to the ledger, then
    /// apply one additive rollup delta per bucket.
    /// </summary>
    private static async Task PersistAsync(GatewayDbContext db, IReadOnlyList<BillingEventRecord> events)
    {
        var eventRepository = new BillingEventRepository(db);
        var appended = new List<BillingEventRecord>();

        foreach (var record in events)
        {
            if (await eventRepository.TryAppendAsync(record))
            {
                appended.Add(record);
            }
        }

        if (appended.Count > 0)
        {
            await new DailyUsageRollupRepository(db).IncrementRollupsAsync(ToDeltas(appended));
        }
    }

    private static List<DailyUsageRollupDelta> ToDeltas(IReadOnlyList<BillingEventRecord> events) =>
        events
            .GroupBy(DailyUsageRollupKey.FromEvent)
            .Select(group => new DailyUsageRollupDelta(
                group.Key.UsageDate,
                group.Key.TenantId,
                group.Key.ModelId,
                group.Key.CostCenter,
                group.Sum(r => r.PromptTokens),
                group.Sum(r => r.CompletionTokens),
                group.Sum(r => r.TotalCost ?? 0m),
                group.Count()))
            .ToList();

    private static Task<BillingReconciliationReport> ReconcileAsync(GatewayDbContext db) =>
        new BillingReconciliationService(
                new BillingEventRepository(db),
                new DailyUsageRollupRepository(db))
            .ReconcileAsync(Day, Day);

    private static BillingEventRecord Event(
        string requestId,
        Guid tenantId,
        string modelId,
        string? costCenter,
        long promptTokens,
        long completionTokens,
        decimal? totalCost,
        DateTimeOffset recordedAt) =>
        new(
            Guid.NewGuid(),
            requestId,
            tenantId,
            Guid.NewGuid(),
            modelId,
            costCenter,
            promptTokens,
            completionTokens,
            null,
            null,
            totalCost,
            100,
            recordedAt);
}
