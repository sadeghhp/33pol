using System.Diagnostics.Metrics;
using Pol33.Core.Billing;
using Pol33.Core.Models.Overview;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Metrics;

public sealed class GatewayFinOpsMetricsTests
{
    [Fact]
    public void Build_UsesTenantSlugAndAnonymousBucket_NotTheTenantId()
    {
        var tenantId = Guid.NewGuid();
        var snapshot = GatewayFinOpsMetrics.Build(
            new FinOpsOverview
            {
                TodayCost = 1.5m,
                MonthToDateCost = 4m,
                UnpricedModelIds = ["local-mock"],
                Budgets =
                [
                    new BudgetStatus
                    {
                        BudgetId = Guid.NewGuid(),
                        TenantId = tenantId,
                        TenantSlug = "acme",
                        Name = "R&D",
                        Limit = 10m,
                        Spent = 10m,
                        Ratio = 1,
                        HardStopEnabled = true,
                    },
                ],
            },
            new PolicyOverview
            {
                Quotas =
                [
                    new QuotaStatus
                    {
                        PartitionKey = tenantId.ToString(),
                        TenantSlug = "acme",
                        Period = "2026-10",
                        Used = 5,
                        Limit = 10,
                        Ratio = 0.5,
                    },
                ],
            },
            new TenantsOverview
            {
                KeyCount = 3,
                AnonymousRequestShare = 0.25,
                TopConsumersMonthToDate =
                [
                    new TenantConsumer { TenantId = null, TenantSlug = "anonymous", Cost = 0.5m },
                    new TenantConsumer { TenantId = tenantId, TenantSlug = null, Cost = 2m },
                ],
                ExpiringKeys = [Key("soon")],
                IdleKeys = [Key("idle"), Key("older")],
            },
            []);

        var tags = AllTagValues(snapshot);
        tags.Should().Contain("acme");
        tags.Should().Contain(BillingMetricLabels.AnonymousTenant);
        tags.Should().Contain(BillingMetricLabels.UnknownTenant);
        tags.Should().NotContain(tenantId.ToString());
        tags.Should().NotContain(tenantId.ToString("N"));

        GatewayFinOpsMetrics.ObserveSpend(snapshot).Select(m => m.Value).Should().Equal(1.5d, 4d);
        GatewayFinOpsMetrics.ObserveUnpriced(snapshot).Single().Value.Should().Be(1);
        GatewayFinOpsMetrics.ObserveKeyCounts(snapshot).Should().Contain(m => Tag(m, "state") == "expiring" && m.Value == 1);
        GatewayFinOpsMetrics.ObserveKeyCounts(snapshot).Should().Contain(m => Tag(m, "state") == "idle" && m.Value == 2);
        GatewayFinOpsMetrics.ObserveBudgets(snapshot).Single().Value.Should().Be(1);
        Tag(GatewayFinOpsMetrics.ObserveBudgets(snapshot).Single(), "hard_stop").Should().Be("true");
    }

    [Fact]
    public void Build_CapsKeySeriesAtTwentyFive_AndNeverEmitsAKeyPrefix()
    {
        var prefix = "sk-33pol-secret";
        var keys = Enumerable.Range(1, 30)
            .Select(i => new FinOpsKeyCost("acme", Guid.NewGuid(), $"laptop-{i}", i % 2 == 0 ? null : "sam", i))
            .ToArray();

        var snapshot = GatewayFinOpsMetrics.Build(null, null, new TenantsOverview(), keys);
        snapshot.Keys.Should().HaveCount(BillingMetricLabels.TopKeyLimit);

        var costs = snapshot.Keys.Select(k => k.Cost).OrderByDescending(c => c).ToArray();
        costs[0].Should().Be(30d);
        costs[^1].Should().Be(6d);
        snapshot.Keys.Select(k => k.Assignee).Should().Contain(BillingMetricLabels.NoAssignee);

        var measurements = GatewayFinOpsMetrics.ObserveKeyCosts(snapshot).ToList();
        measurements.Should().HaveCount(BillingMetricLabels.TopKeyLimit);
        foreach (var measurement in measurements)
        {
            var names = new List<string>();
            var values = new List<string?>();
            foreach (var tag in measurement.Tags)
            {
                names.Add(tag.Key);
                values.Add(tag.Value?.ToString());
            }

            names.Should().BeEquivalentTo(["tenant", "key_label", "assignee", "key_id"]);
            values.Should().NotContain(prefix);
            names.Should().NotContain(name => name.Contains("prefix", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Build_WithoutADatabaseSnapshot_ExportsNothing()
    {
        var snapshot = GatewayFinOpsMetrics.Build(null, null, null, []);

        GatewayFinOpsMetrics.ObserveSpend(snapshot).Should().BeEmpty();
        GatewayFinOpsMetrics.ObserveUnpriced(snapshot).Should().BeEmpty();
        GatewayFinOpsMetrics.ObserveAnonymousShare(snapshot).Should().BeEmpty();
        GatewayFinOpsMetrics.ObserveKeyCounts(snapshot).Should().BeEmpty();
    }

    [Fact]
    public void RecordBilledCost_AddsTheEventCostUnderTenantModelAndCostCenter()
    {
        var measurements = new List<(double Value, string Tenant, string Model, string CostCenter)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == GatewayMeters.MeterName &&
                    instrument.Name == "gateway_billed_cost_dollars_total")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            string tenant = "", model = "", costCenter = "";
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "tenant":
                        tenant = tag.Value?.ToString() ?? "";
                        break;
                    case "model":
                        model = tag.Value?.ToString() ?? "";
                        break;
                    case "cost_center":
                        costCenter = tag.Value?.ToString() ?? "";
                        break;
                }
            }

            measurements.Add((measurement, tenant, model, costCenter));
        });
        listener.Start();

        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());
        collector.RecordBilledCost("acme", "gpt-4o", "eng", 1.25);
        collector.RecordBilledCost("acme", "gpt-4o", "eng", 0);

        listener.Dispose();

        measurements.Should().ContainSingle();
        measurements[0].Value.Should().Be(1.25);
        measurements[0].Tenant.Should().Be("acme");
        measurements[0].Model.Should().Be("gpt-4o");
        measurements[0].CostCenter.Should().Be("eng");
    }

    private static KeySummary Key(string label) => new()
    {
        Id = Guid.NewGuid(),
        KeyPrefix = "not-exported",
        Label = label,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static List<string?> AllTagValues(FinOpsMetricSnapshot snapshot)
    {
        var values = new List<string?>();
        foreach (var measurement in GatewayFinOpsMetrics.ObserveBudgets(snapshot)
                     .Concat(GatewayFinOpsMetrics.ObserveQuotas(snapshot))
                     .Concat(GatewayFinOpsMetrics.ObserveTenantCosts(snapshot))
                     .Concat(GatewayFinOpsMetrics.ObserveKeyCosts(snapshot)))
        {
            foreach (var tag in measurement.Tags)
            {
                values.Add(tag.Value?.ToString());
            }
        }

        return values;
    }

    private static string? Tag<T>(Measurement<T> measurement, string key) where T : struct
    {
        foreach (var tag in measurement.Tags)
        {
            if (tag.Key == key)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }
}
