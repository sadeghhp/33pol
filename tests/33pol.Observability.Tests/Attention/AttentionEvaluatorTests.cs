using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models.Overview;
using Pol33.Observability.Attention;

namespace Pol33.Observability.Tests.Attention;

public sealed class AttentionEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static AttentionEvaluator Create(Action<OverviewAttentionOptions>? configure = null)
    {
        var options = new GatewayOptions();
        configure?.Invoke(options.Overview.Attention);
        return new AttentionEvaluator(Options.Create(options));
    }

    private static WindowStats Window(string label, long requests, long errors) => new()
    {
        Window = label,
        Requests = requests,
        Errors = errors,
        ErrorRate = requests == 0 ? 0 : (double)errors / requests,
    };

    private static BackendOverview Backend(string id, bool healthy = true, string circuit = "closed", int inFlight = 0, int max = 4, int queued = 0, int maxQueued = 0) => new()
    {
        ModelId = id,
        Url = "http://" + id,
        IsHealthy = healthy,
        CircuitState = circuit,
        InFlight = inFlight,
        MaxConcurrent = max,
        Queued = queued,
        MaxQueued = maxQueued,
    };

    [Fact]
    public void Evaluate_ErrorRateAboveThreshold_IsListedOnlyAfterItHeldForTheConfiguredDuration()
    {
        var sut = Create(o => o.ErrorRateForSeconds = 300);
        var inputs = new AttentionInputs { Now = Now, Windows = [Window("5m", 100, 10)] };

        sut.Evaluate(inputs).Should().BeEmpty("the condition was just observed");
        var later = sut.Evaluate(inputs with { Now = Now.AddSeconds(300) });

        var item = later.Should().ContainSingle().Subject;
        item.Code.Should().Be("error_rate_high");
        item.Severity.Should().Be(AttentionItem.SeverityWarning);
        item.SinceUtc.Should().Be(Now, "since is the first observation, not when it was listed");
        item.Link!.Tab.Should().Be("errors");
    }

    [Fact]
    public void Evaluate_ConditionCleared_ResetsHysteresis()
    {
        var sut = Create(o => o.ErrorRateForSeconds = 60);
        sut.Evaluate(new AttentionInputs { Now = Now, Windows = [Window("5m", 100, 10)] });
        sut.Evaluate(new AttentionInputs { Now = Now.AddSeconds(30), Windows = [Window("5m", 100, 0)] });

        var items = sut.Evaluate(new AttentionInputs { Now = Now.AddSeconds(61), Windows = [Window("5m", 100, 10)] });

        items.Should().BeEmpty("the clock restarted when the condition cleared");
    }

    [Fact]
    public void Evaluate_LowSampleCount_SkipsErrorRate()
    {
        var sut = Create(o => { o.ErrorRateForSeconds = 0; o.ErrorRateMinRequests = 20; });

        sut.Evaluate(new AttentionInputs { Now = Now, Windows = [Window("5m", 5, 5)] }).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_NoHealthyBackends_IsCriticalAndPerBackendWarningsFollow()
    {
        var sut = Create(o => o.BackendUnhealthyForSeconds = 0);

        var items = sut.Evaluate(new AttentionInputs
        {
            Now = Now,
            Backends = [Backend("a", healthy: false), Backend("b", healthy: false)],
        });

        items.Select(i => i.Code).Should().Equal("no_healthy_backends", "backend_unhealthy", "backend_unhealthy");
        items[0].Severity.Should().Be(AttentionItem.SeverityCritical);
        items[1].ModelId.Should().Be("a");
        items[1].Link!.Params!["model"].Should().Be("a");
    }

    [Fact]
    public void Evaluate_OpenCircuitAndSaturatedBulkhead_AreListed()
    {
        var sut = Create(o => o.CircuitOpenForSeconds = 0);
        var inputs = new AttentionInputs
        {
            Now = Now,
            Backends = [Backend("a", circuit: "open"), Backend("b", inFlight: 4, max: 4, maxQueued: 0)],
        };

        sut.Evaluate(inputs);
        var items = sut.Evaluate(inputs with { Now = Now.AddSeconds(60) });

        items.Select(i => i.Code).Should().BeEquivalentTo(["circuit_open", "bulkhead_saturated"]);
    }

    [Fact]
    public void Evaluate_UsageWriter_BacklogAndDrops()
    {
        var sut = Create(o => o.UsageWriterQueueWarn = 100);

        sut.Evaluate(new AttentionInputs { Now = Now, Pipeline = new PipelineOverview { UsageWriterQueueDepth = 500, UsageWriterDropped = 0 } });
        var items = sut.Evaluate(new AttentionInputs
        {
            Now = Now.AddSeconds(300),
            Pipeline = new PipelineOverview { UsageWriterQueueDepth = 500, UsageWriterDropped = 3 },
        });

        items.Select(i => i.Code).Should().Equal("usage_events_dropped", "usage_writer_backlog");
        items[0].Severity.Should().Be(AttentionItem.SeverityCritical);

        var muchLater = sut.Evaluate(new AttentionInputs
        {
            Now = Now.AddMinutes(20),
            Pipeline = new PipelineOverview { UsageWriterQueueDepth = 0, UsageWriterDropped = 3 },
        });
        muchLater.Should().BeEmpty("drops older than five minutes stop being urgent");
    }

    [Fact]
    public void Evaluate_ParseFailureRate_UsesSuccessiveObservations()
    {
        var sut = Create(o => o.UsageParseFailureRatePerSecondWarn = 0.1);

        sut.Evaluate(new AttentionInputs { Now = Now, Pipeline = new PipelineOverview { UsageParseFailures = 0 } });
        sut.Evaluate(new AttentionInputs { Now = Now.AddSeconds(60), Pipeline = new PipelineOverview { UsageParseFailures = 60 } });
        var items = sut.Evaluate(new AttentionInputs { Now = Now.AddSeconds(360), Pipeline = new PipelineOverview { UsageParseFailures = 360 } });

        items.Should().ContainSingle(i => i.Code == "usage_parse_failures");
    }

    [Fact]
    public void Evaluate_FinOps_ReconciliationUnpricedAndBudgets()
    {
        var sut = Create();
        var finops = new FinOpsOverview
        {
            Currency = "USD",
            UnpricedModelIds = ["m1"],
            Reconciliation = new ReconciliationStatus { Enabled = true, LastRunUtc = Now.AddHours(-4), DiscrepancyCount = 2, AbsoluteCostDrift = 0.5m },
            Budgets =
            [
                new BudgetStatus { BudgetId = Guid.NewGuid(), TenantId = Guid.NewGuid(), TenantSlug = "acme", Name = "R&D", Limit = 100, Spent = 92, Ratio = 0.92, WarningRatio = 0.8, HardStopEnabled = true },
                new BudgetStatus { BudgetId = Guid.NewGuid(), TenantId = Guid.NewGuid(), TenantSlug = "beta", Name = "Ops", Limit = 100, Spent = 120, Ratio = 1.2, WarningRatio = 0.8, HardStopEnabled = true },
                new BudgetStatus { BudgetId = Guid.NewGuid(), TenantId = Guid.NewGuid(), TenantSlug = "gamma", Name = "Low", Limit = 100, Spent = 10, Ratio = 0.1, WarningRatio = 0.8 },
            ],
        };

        var first = sut.Evaluate(new AttentionInputs { Now = Now, FinOps = finops });
        var items = sut.Evaluate(new AttentionInputs { Now = Now.AddMinutes(15), FinOps = finops });

        first.Select(i => i.Code).Should().NotContain("reconciliation_discrepancies", "that rule waits 15 minutes");
        items.Select(i => i.Code).Should().Equal(
            "budget_hard_stop", "reconciliation_discrepancies", "reconciliation_stalled", "budget_near_limit", "unpriced_models");
        items.Single(i => i.Code == "budget_near_limit").Title.Should().Contain("acme").And.Contain("92%");
    }

    [Fact]
    public void Evaluate_ReconciliationThatNeverRan_IsNotStalled()
    {
        var sut = Create();
        var finops = new FinOpsOverview { Reconciliation = new ReconciliationStatus { Enabled = true, LastRunUtc = null } };

        sut.Evaluate(new AttentionInputs { Now = Now, FinOps = finops }).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_ControlPlaneAndTenants_ListSecretsBackupsAndKeys()
    {
        var sut = Create(o => o.BackupStaleAfterDays = 7);
        var items = sut.Evaluate(new AttentionInputs
        {
            Now = Now,
            ControlPlane = new ControlPlaneOverview
            {
                Secrets = new SecretsVerificationStatus { HasRun = true, Total = 3, Undecryptable = 1 },
                Database = new DatabaseStatus { Configured = true },
                LastBackup = new BackupStatus { AttemptedAtUtc = Now.AddDays(-10), Succeeded = true },
            },
            Tenants = new TenantsOverview
            {
                ExpiringKeys = [new KeySummary { KeyPrefix = "sk-1", Label = "ci" }],
                IdleKeys = [new KeySummary { KeyPrefix = "sk-2" }, new KeySummary { KeyPrefix = "sk-3" }],
            },
        });

        items.Select(i => i.Code).Should().Equal("secrets_undecryptable", "backup_stale", "key_expiring", "key_idle");
        items[0].Severity.Should().Be(AttentionItem.SeverityCritical);
        items[1].Title.Should().Contain("10 days old");
    }

    /// <summary>
    /// The health store knows when the backend went down; the item must say that, not when this
    /// process first happened to evaluate it — and a fault older than the hold time lists at once.
    /// </summary>
    [Fact]
    public void Evaluate_UnhealthyBackend_IsDatedFromTheHealthStoreTransition()
    {
        var sut = Create(o => o.BackendUnhealthyForSeconds = 60);
        var down = Backend("a", healthy: false) with { LastTransitionUtc = Now.AddHours(-2) };

        var item = sut.Evaluate(new AttentionInputs { Now = Now, Backends = [down] }).Should().ContainSingle().Subject;

        item.Code.Should().Be("backend_unhealthy");
        item.SinceUtc.Should().Be(Now.AddHours(-2));
    }

    [Fact]
    public void Evaluate_Disabled_ReturnsNothing()
    {
        var sut = Create(o => o.Enabled = false);

        sut.Evaluate(new AttentionInputs { Now = Now, Backends = [Backend("a", healthy: false)] }).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_OrdersBySeverityThenAge()
    {
        var sut = Create(o => { o.BackendUnhealthyForSeconds = 0; o.CircuitOpenForSeconds = 0; });
        sut.Evaluate(new AttentionInputs { Now = Now, Backends = [Backend("old", circuit: "open"), Backend("ok")] });
        var items = sut.Evaluate(new AttentionInputs
        {
            Now = Now.AddSeconds(10),
            Backends = [Backend("old", circuit: "open"), Backend("ok", healthy: false), Backend("newer", healthy: false)],
        });

        items.Select(i => i.Code).Should().Equal("circuit_open", "backend_unhealthy", "backend_unhealthy");
        items[0].SinceUtc.Should().Be(Now);
    }
}
