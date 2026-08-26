using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Observability.RateLimiting;

namespace Pol33.Observability.Tests.RateLimiting;

/// <summary>
/// The usage report: per user, per model, per user-and-model, and where limits are actually being
/// hit.
/// </summary>
public sealed class RateLimitUsageTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildReport_SplitsUsageByTenantModelTenantAndModel()
    {
        var tracker = Create();

        Record(tracker, "acme", "key-1", "gpt-4", admitted: true, times: 6);
        Record(tracker, "acme", "key-1", "llama", admitted: true, times: 4);
        Record(tracker, "globex", "key-2", "gpt-4", admitted: true, times: 3);

        var report = tracker.BuildReport(60, 25, Now);

        report.ByTenantModel.Single(r => r.Key == "acme|gpt-4").Requests.Should().Be(6);
        report.ByTenant.Single(r => r.Key == "acme").Requests.Should().Be(10);
        report.ByModel.Single(r => r.Key == "gpt-4").Requests.Should().Be(9);
        report.ByApiKey.Single(r => r.Key == "key-1").Requests.Should().Be(10);
        report.Totals.Requests.Should().Be(13);
        report.Totals.Rejected.Should().Be(0);
    }

    [Fact]
    public void BuildReport_CountsRejectionsAgainstTheRowAndTheTotals()
    {
        var tracker = Create();

        Record(tracker, "acme", "key-1", "gpt-4", admitted: true, times: 2);
        Record(tracker, "acme", "key-1", "gpt-4", admitted: false, times: 8, scope: RateLimitScope.Model);

        var report = tracker.BuildReport(60, 25, Now);

        var row = report.ByTenantModel.Single();
        row.Requests.Should().Be(10);
        row.Admitted.Should().Be(2);
        row.Rejected.Should().Be(8);
        report.Totals.RejectionRate.Should().BeApproximately(0.8, 0.001);
    }

    /// <summary>
    /// A limit hit is attributed to the identity the scope that refused actually counts. Attributing
    /// every hit to the caller would make one saturated model look like a hundred misbehaving
    /// tenants.
    /// </summary>
    [Fact]
    public void BuildReport_AttributesAViolationToTheScopeThatRefused()
    {
        var tracker = Create();

        Record(tracker, "acme", "key-1", "gpt-4", admitted: false, times: 5, scope: RateLimitScope.Model);
        Record(tracker, "acme", "key-1", "gpt-4", admitted: false, times: 2, scope: RateLimitScope.Tenant);

        var violations = tracker.BuildReport(60, 25, Now).Violations;

        violations.Single(v => v.Scope == "model").Key.Should().Be("gpt-4");
        violations.Single(v => v.Scope == "model").Hits.Should().Be(5);
        violations.Single(v => v.Scope == "tenant").Key.Should().Be("acme");
        violations.Single(v => v.Scope == "tenant").Hits.Should().Be(2);
    }

    /// <summary>
    /// Utilization is the whole point of the report: observed load against the limit actually in
    /// force. Over 1 is normal for a row being refused — the numerator counts attempts, not
    /// admissions.
    /// </summary>
    [Fact]
    public void BuildReport_ReportsLoadAgainstTheEffectiveLimit()
    {
        var tracker = Create();

        Record(tracker, "acme", "key-1", "gpt-4", admitted: true, times: 60, configuredRpm: 100, effectiveRpm: 40);

        var row = tracker.BuildReport(60, 25, Now).ByTenant.Single();

        row.RequestsPerMinute.Should().BeApproximately(1.0, 0.001);
        row.ConfiguredRpm.Should().Be(100);
        row.EffectiveRpm.Should().Be(40, "the report shows what was enforced, not only what was configured");
        row.Utilization.Should().BeApproximately(1.0 / 40, 0.0001);
    }

    /// <summary>A row with no governing limit reports no utilization rather than a misleading zero.</summary>
    [Fact]
    public void BuildReport_WithNoLimitInForce_ReportsNoUtilization()
    {
        var tracker = Create();

        Record(tracker, "acme", "key-1", "gpt-4", admitted: true, times: 1, configuredRpm: 0, effectiveRpm: 0);

        tracker.BuildReport(60, 25, Now).ByTenant.Single().Utilization.Should().BeNull();
    }

    /// <summary>
    /// Past the ceiling, new keys are ignored rather than evicting existing ones — a flood of
    /// one-off callers must not blank out the tenants an operator is watching.
    /// </summary>
    [Fact]
    public void Record_PastTheKeyCeiling_KeepsTheExistingKeys()
    {
        var tracker = Create(maxKeys: 10);

        Record(tracker, "watched", "key-1", "gpt-4", admitted: true, times: 5);

        for (var i = 0; i < 500; i++)
        {
            Record(tracker, $"flood-{i}", null, null, admitted: true, times: 1);
        }

        var byTenant = tracker.BuildReport(60, 100, Now).ByTenant;
        byTenant.Should().HaveCountLessThanOrEqualTo(10);
        byTenant.Should().Contain(r => r.Key == "watched");
    }

    [Fact]
    public void Reset_ClearsEverySection()
    {
        var tracker = Create();
        Record(tracker, "acme", "key-1", "gpt-4", admitted: false, times: 3, scope: RateLimitScope.Tenant);

        tracker.Reset();

        var report = tracker.BuildReport(60, 25, Now);
        report.Totals.Requests.Should().Be(0);
        report.Violations.Should().BeEmpty();
    }

    private static RateLimitUsageTracker Create(int maxKeys = 500) =>
        new(
            Options.Create(new RateLimitingOptions { UsageReportMaxKeys = maxKeys }),
            timeProvider: new FixedTimeProvider(Now));


    /// <summary>
    /// The two refusal counters used to be published as a hard-coded zero, so the report stated
    /// "no concurrency refusals" whether or not any had happened. They are summed from the same
    /// single dimension the totals come from, so a request refused once is counted once however many
    /// sections it also appears under.
    /// </summary>
    [Fact]
    public void BuildReport_SplitsTheTotalsIntoRateAndConcurrencyRefusals()
    {
        var tracker = Create();

        Record(tracker, "acme", "key-1", "gpt-4", admitted: true, times: 5);
        Record(tracker, "acme", "key-1", "gpt-4", admitted: false, times: 3, scope: RateLimitScope.Tenant);
        Record(
            tracker,
            "acme",
            "key-1",
            "gpt-4",
            admitted: false,
            times: 2,
            scope: RateLimitScope.Model,
            configuredRpm: 0,
            effectiveRpm: 0,
            control: RateLimitControl.Concurrency);

        var totals = tracker.BuildReport(60, 25, Now).Totals;

        totals.Requests.Should().Be(10);
        totals.Admitted.Should().Be(5);
        totals.Rejected.Should().Be(5);
        totals.RateRejected.Should().Be(3);
        totals.ConcurrencyRejected.Should().Be(2);
    }

    /// <summary>
    /// A concurrency decision was made against a slot count, not a per-minute rate, so it carries no
    /// rpm at all. The router used to pass <c>MaxConcurrentStreams</c> into both rpm fields, and
    /// because the tier a key is held to is last-writer-wins, one streaming refusal replaced that
    /// key's real limit with a number of streams — silently rescaling its whole utilisation column.
    /// </summary>
    [Fact]
    public void Record_AConcurrencyDecision_DoesNotOverwriteTheKeysRateLimit()
    {
        var tracker = Create();

        Record(tracker, "acme", "key-1", "gpt-4", admitted: true, times: 60, configuredRpm: 600, effectiveRpm: 600);
        Record(
            tracker,
            "acme",
            "key-1",
            "gpt-4",
            admitted: false,
            times: 1,
            scope: RateLimitScope.Model,
            configuredRpm: 0,
            effectiveRpm: 0,
            control: RateLimitControl.Concurrency);

        var row = tracker.BuildReport(60, 25, Now).ByTenant.Single(r => r.Key == "acme");

        row.ConfiguredRpm.Should().Be(600);
        row.EffectiveRpm.Should().Be(600);
        row.Utilization.Should().BeApproximately(61 / 60.0 / 600, 1e-9);
    }

    private static void Record(
        RateLimitUsageTracker tracker,
        string? tenant,
        string? apiKey,
        string? model,
        bool admitted,
        int times,
        RateLimitScope? scope = null,
        int configuredRpm = 100,
        int effectiveRpm = 100,
        RateLimitControl control = RateLimitControl.Rate)
    {
        for (var i = 0; i < times; i++)
        {
            tracker.Record(new RateLimitUsageEvent(
                tenant,
                apiKey,
                model,
                admitted,
                scope,
                control,
                configuredRpm,
                effectiveRpm));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
