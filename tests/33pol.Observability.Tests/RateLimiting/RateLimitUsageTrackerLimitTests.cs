using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.RateLimiting;
using Pol33.Observability.RateLimiting;

namespace Pol33.Observability.Tests.RateLimiting;

/// <summary>
/// The parts of the report that say what each configured limit did, whether the counters are
/// complete, and how the window looked minute by minute.
/// </summary>
public sealed class RateLimitUsageTrackerLimitTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 30, TimeSpan.Zero);

    // --- Saturation -----------------------------------------------------------------------------

    [Fact]
    public void Tracker_BelowCapacity_IsNotSaturated()
    {
        var (tracker, _) = Create(maxKeys: 10);
        Admit(tracker, "t1");

        var report = tracker.BuildReport(60, 25, Now).Tracker;

        report.IsSaturated.Should().BeFalse();
        report.MaxKeysPerDimension.Should().Be(10);
        report.Dimensions.Single(d => d.Name == "tenants").TrackedKeys.Should().Be(1);
        report.Dimensions.Should().OnlyContain(d => d.DroppedDecisions == 0 && d.FirstDroppedUtc == null);
    }

    /// <summary>Full is not yet lossy: nothing has been turned away, so every zero is still a real zero.</summary>
    [Fact]
    public void Tracker_AtCapacityWithNothingDropped_IsFullButNotSaturated()
    {
        var (tracker, _) = Create(maxKeys: 10);
        for (var i = 0; i < 10; i++)
        {
            Admit(tracker, "t" + i);
        }

        var report = tracker.BuildReport(60, 25, Now).Tracker;

        report.Dimensions.Single(d => d.Name == "tenants").AtCapacity.Should().BeTrue();
        report.IsSaturated.Should().BeFalse();
    }

    [Fact]
    public void Tracker_FirstDroppedSubject_SaturatesAndStampsWhen()
    {
        var (tracker, time) = Create(maxKeys: 10);
        for (var i = 0; i < 10; i++)
        {
            Admit(tracker, "t" + i);
        }

        time.Advance(TimeSpan.FromMinutes(2));
        Admit(tracker, "one-too-many");

        var report = tracker.BuildReport(60, 25, time.GetUtcNow());
        var tenants = report.Tracker.Dimensions.Single(d => d.Name == "tenants");

        report.Tracker.IsSaturated.Should().BeTrue();
        tenants.DroppedDecisions.Should().Be(1);
        tenants.FirstDroppedUtc.Should().Be(Now.AddMinutes(2));
        report.ByTenant.Should().NotContain(r => r.Key == "one-too-many");
    }

    [Fact]
    public void Tracker_LaterDrops_AreCountedAndTheFirstTimestampStays()
    {
        var (tracker, time) = Create(maxKeys: 10);
        for (var i = 0; i < 10; i++)
        {
            Admit(tracker, "t" + i);
        }

        Admit(tracker, "late-1");
        time.Advance(TimeSpan.FromMinutes(5));
        Admit(tracker, "late-2");
        Admit(tracker, "late-1");

        var tenants = tracker.BuildReport(60, 25, time.GetUtcNow()).Tracker.Dimensions.Single(d => d.Name == "tenants");

        tenants.DroppedDecisions.Should().Be(3);
        tenants.FirstDroppedUtc.Should().Be(Now);
    }

    /// <summary>The totals come from a ring no ceiling applies to, so they stay exact when a section is full.</summary>
    [Fact]
    public void Totals_StayExact_WhileTheTenantSectionIsSaturated()
    {
        var (tracker, _) = Create(maxKeys: 10);
        for (var i = 0; i < 25; i++)
        {
            Admit(tracker, "t" + i);
        }

        var report = tracker.BuildReport(60, 25, Now);

        report.ByTenant.Sum(r => r.Requests).Should().Be(10);
        report.Totals.Requests.Should().Be(25);
    }

    [Fact]
    public void Reset_ClearsSaturationAndRestartsTheTrackingClock()
    {
        var (tracker, time) = Create(maxKeys: 10);
        for (var i = 0; i < 12; i++)
        {
            Admit(tracker, "t" + i);
        }

        time.Advance(TimeSpan.FromMinutes(1));
        tracker.Reset();

        var report = tracker.BuildReport(60, 25, time.GetUtcNow()).Tracker;
        report.IsSaturated.Should().BeFalse();
        report.TrackingSinceUtc.Should().Be(Now.AddMinutes(1));
        report.Dimensions.Should().OnlyContain(d => d.TrackedKeys == 0 && d.DroppedDecisions == 0);
    }

    /// <summary>
    /// The ceiling is counted alongside each dictionary rather than read from it, because
    /// ConcurrentDictionary.Count locks every bucket and the check sits on the request path. A
    /// counter that is not cleared with its dictionary would leave the tracker permanently full —
    /// silently dropping everything while reporting nothing tracked.
    /// </summary>
    [Fact]
    public void Reset_FreesTheKeyCeiling_SoCountingStartsAgain()
    {
        var (tracker, _) = Create(maxKeys: 10);
        for (var i = 0; i < 40; i++)
        {
            Admit(tracker, "t" + i);
            tracker.RecordRateStage([Rule(RateLimitScope.ApiKey, "k:" + i, "api_key:k" + i)], RateLimitStageOutcome.Charged);
            tracker.Record(new RateLimitUsageEvent("t" + i, null, null, false, RateLimitScope.Tenant, RateLimitControl.Rate, 100, 100));
        }

        tracker.Reset();

        Admit(tracker, "after-reset");
        tracker.RecordRateStage([Rule(RateLimitScope.ApiKey, "k:after", "api_key:after")], RateLimitStageOutcome.Charged);
        tracker.Record(new RateLimitUsageEvent("after-reset", null, null, false, RateLimitScope.Tenant, RateLimitControl.Rate, 100, 100));

        var report = tracker.BuildReport(60, 25, Now);
        report.ByTenant.Should().ContainSingle().Which.Key.Should().Be("after-reset");
        report.Limits.Should().ContainSingle().Which.LimitId.Should().Be("api_key:after");
        report.Violations.Should().ContainSingle().Which.Key.Should().Be("after-reset");
        report.Tracker.IsSaturated.Should().BeFalse();
    }

    /// <summary>
    /// The four rate counters partition the evaluations exactly, whatever mix of outcomes a limit
    /// saw. A limit that is asked for a token either keeps it, is the one that refused, or gives it
    /// back — there is no fourth case, and none of them may be counted twice.
    /// </summary>
    [Fact]
    public void Limit_Counters_PartitionEveryEvaluationExactlyOnce()
    {
        var (tracker, _) = Create();
        RateLimitRule[] pair = [Rule(RateLimitScope.Global, "g", "global:*"), Rule(RateLimitScope.Tenant, "t:acme", "tenant:acme")];

        for (var i = 0; i < 5; i++)
        {
            tracker.RecordRateStage(pair, RateLimitStageOutcome.Charged);
        }

        for (var i = 0; i < 3; i++)
        {
            tracker.RecordRateStage(pair, RateLimitStageOutcome.Refused, refusedPartitionKey: "t:acme");
        }

        tracker.RecordRateStage(pair, RateLimitStageOutcome.RefundedByLaterStage);

        var limits = tracker.BuildReport(60, 25, Now).Limits;
        limits.Should().HaveCount(2);
        limits.Should().OnlyContain(l => l.Evaluations == l.Charged + l.RefusedByRate + l.PassedThenRefunded);

        // And the split itself: the global rule passed every time and gave its token back four
        // times; the tenant rule is the one that refused.
        var global = limits.Single(l => l.LimitId == "global:*");
        global.Should().Match<RateLimitLimitUsageRow>(l => l.Evaluations == 9 && l.Charged == 5 && l.RefusedByRate == 0 && l.PassedThenRefunded == 4);
        var tenant = limits.Single(l => l.LimitId == "tenant:acme");
        tenant.Should().Match<RateLimitLimitUsageRow>(l => l.Evaluations == 9 && l.Charged == 5 && l.RefusedByRate == 3 && l.PassedThenRefunded == 1);
    }

    [Fact]
    public void LimitsDimension_SaturatesOnItsOwn()
    {
        var (tracker, _) = Create(maxKeys: 10);
        for (var i = 0; i < 11; i++)
        {
            tracker.RecordRateStage([Rule(RateLimitScope.ApiKey, "k:" + i, "api_key:k" + i)], RateLimitStageOutcome.Charged);
        }

        var report = tracker.BuildReport(60, 25, Now);

        report.Limits.Should().HaveCount(10);
        report.Tracker.Dimensions.Single(d => d.Name == "limits").DroppedDecisions.Should().Be(1);
        report.Tracker.IsSaturated.Should().BeTrue();
    }

    // --- Per-limit ------------------------------------------------------------------------------

    [Fact]
    public void RateStage_Charged_ChargesEveryLimitInTheSet()
    {
        var (tracker, _) = Create();
        RateLimitRule[] rules =
        [
            Rule(RateLimitScope.Global, "g", "global:*", rpm: 1000),
            Rule(RateLimitScope.Tenant, "t:acme", "plan:pro", rpm: 120),
        ];

        tracker.RecordRateStage(rules, RateLimitStageOutcome.Charged);
        tracker.RecordRateStage(rules, RateLimitStageOutcome.Charged);

        var limits = tracker.BuildReport(60, 25, Now).Limits;
        limits.Should().HaveCount(2);
        limits.Should().OnlyContain(l => l.Evaluations == 2 && l.Charged == 2 && l.RefusedByRate == 0 && l.PassedThenRefunded == 0);
    }

    /// <summary>
    /// Three different things happen to three limits on one refused request, and one counter cannot
    /// mean all of them: the one before passed and was refunded, the one that refused is the
    /// refusal, and the one after was never asked.
    /// </summary>
    [Fact]
    public void RateStage_Refused_SeparatesRefundedRefusedAndNeverAsked()
    {
        var (tracker, _) = Create();
        RateLimitRule[] rules =
        [
            Rule(RateLimitScope.Global, "g", "global:*"),
            Rule(RateLimitScope.Tenant, "t:acme", "tenant:acme"),
            Rule(RateLimitScope.ApiKey, "k:1", "api_key:k1"),
        ];

        tracker.RecordRateStage(rules, RateLimitStageOutcome.Refused, refusedPartitionKey: "t:acme");

        var limits = tracker.BuildReport(60, 25, Now).Limits.ToDictionary(l => l.LimitId);
        limits["global:*"].Should().Match<RateLimitLimitUsageRow>(l =>
            l.Evaluations == 1 && l.Charged == 0 && l.RefusedByRate == 0 && l.PassedThenRefunded == 1);
        limits["tenant:acme"].Should().Match<RateLimitLimitUsageRow>(l =>
            l.Evaluations == 1 && l.RefusedByRate == 1 && l.PassedThenRefunded == 0);
        limits.Should().NotContainKey("api_key:k1");
    }

    [Fact]
    public void RateStage_RefundedByLaterStage_CountsAPassNotACharge()
    {
        var (tracker, _) = Create();

        tracker.RecordRateStage([Rule(RateLimitScope.Tenant, "t:acme", "tenant:acme")], RateLimitStageOutcome.RefundedByLaterStage);

        var row = tracker.BuildReport(60, 25, Now).Limits.Single();
        row.Charged.Should().Be(0);
        row.PassedThenRefunded.Should().Be(1);
    }

    [Fact]
    public void RateStage_SkipsARuleWithNoCapacityOrNoLimitId()
    {
        var (tracker, _) = Create();

        tracker.RecordRateStage(
            [
                new RateLimitRule(RateLimitScope.Tenant, "t:a", new RateLimitPolicy(0, 0, 4)) { LimitId = "tenant:a" },
                new RateLimitRule(RateLimitScope.Tenant, "t:b", new RateLimitPolicy(10, 0, 0)),
            ],
            RateLimitStageOutcome.Charged);

        tracker.BuildReport(60, 25, Now).Limits.Should().BeEmpty();
    }

    /// <summary>The rates are exact per limit: adaptive scaling shows up as configured vs effective.</summary>
    [Fact]
    public void Limit_ReportsConfiguredAndEffectiveRate_AndPeakUtilisationForASingleBucket()
    {
        var (tracker, time) = Create();
        var adapted = new RateLimitRule(RateLimitScope.Model, "m:gpt-4", new RateLimitPolicy(50, 0, 0), 100, 0.5)
        {
            LimitId = "model:gpt-4",
        };

        for (var i = 0; i < 40; i++)
        {
            tracker.RecordRateStage([adapted], RateLimitStageOutcome.Charged);
        }

        time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 10; i++)
        {
            tracker.RecordRateStage([adapted], RateLimitStageOutcome.Charged);
        }

        var row = tracker.BuildReport(60, 25, time.GetUtcNow()).Limits.Single();
        row.ConfiguredRpm.Should().Be(100);
        row.EffectiveRpm.Should().Be(50);
        row.SingleBucket.Should().BeTrue();
        row.PeakChargedInOneMinute.Should().Be(40);
        row.PeakMinuteUtc.Should().Be(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        row.PeakUtilization.Should().BeApproximately(0.8, 0.0001);
        row.ChargedPerMinute.Should().BeApproximately(50.0 / 60, 0.0001);
        row.LastDecisionUtc.Should().Be(time.GetUtcNow());
    }

    /// <summary>
    /// A tier is one number applied to many buckets. Its row sums every caller, so dividing that by
    /// the limit would invent a utilisation nobody is at.
    /// </summary>
    [Theory]
    [InlineData("default")]
    [InlineData("plan:pro")]
    public void Limit_ForATier_HasNoUtilisation(string limitId)
    {
        var (tracker, _) = Create();
        tracker.RecordRateStage([Rule(RateLimitScope.Tenant, "t:acme", limitId)], RateLimitStageOutcome.Charged);

        var row = tracker.BuildReport(60, 25, Now).Limits.Single();
        row.SingleBucket.Should().BeFalse();
        row.PeakUtilization.Should().BeNull();
        row.Scope.Should().Be(limitId.Split(':')[0]);
    }

    [Fact]
    public void Limit_KeepsAModelRulesAnonymousBucketApart()
    {
        var (tracker, _) = Create();
        tracker.RecordRateStage([Rule(RateLimitScope.Model, "m:gpt-4", "model:gpt-4")], RateLimitStageOutcome.Charged);
        tracker.RecordRateStage(
            [Rule(RateLimitScope.Model, "m!:gpt-4", "model:gpt-4") with { AnonymousBucket = true }],
            RateLimitStageOutcome.Charged);

        var rows = tracker.BuildReport(60, 25, Now).Limits;
        rows.Should().HaveCount(2);
        rows.Select(r => r.AnonymousBucket).Should().BeEquivalentTo([false, true]);
    }

    [Fact]
    public void StreamStage_CountsStartsUnderEveryCap_AndARefusalOnlyUnderTheFullOne()
    {
        var (tracker, _) = Create();
        RateLimitRule[] rules =
        [
            // The tenant's rate comes from its plan and its cap from an override: two controls.
            new RateLimitRule(RateLimitScope.Tenant, "t:acme", new RateLimitPolicy(100, 0, 4))
            {
                LimitId = "plan:pro",
                StreamLimitId = "tenant:acme",
            },
            new RateLimitRule(RateLimitScope.Model, "m:gpt-4", new RateLimitPolicy(100, 0, 2)) { LimitId = "model:gpt-4", StreamLimitId = "model:gpt-4" },
            new RateLimitRule(RateLimitScope.ApiKey, "k:1", new RateLimitPolicy(100, 0, 0)) { LimitId = "api_key:k1" },
        ];

        tracker.RecordStreamStage(rules);
        tracker.RecordStreamStage(rules, refusedPartitionKey: "m:gpt-4");

        var limits = tracker.BuildReport(60, 25, Now).Limits.ToDictionary(l => l.LimitId);
        limits["tenant:acme"].StreamsStarted.Should().Be(1, "the slot taken and released on the refused request is not a started stream");
        limits["tenant:acme"].RefusedByStreams.Should().Be(0);
        limits["model:gpt-4"].StreamsStarted.Should().Be(1);
        limits["model:gpt-4"].RefusedByStreams.Should().Be(1);
        limits.Should().NotContainKey("plan:pro");
        limits.Should().NotContainKey("api_key:k1");
    }

    // --- Protective -----------------------------------------------------------------------------

    [Fact]
    public void Protective_WithNoActivity_ReportsBothScopesAtZero()
    {
        var (tracker, _) = Create();

        var rows = tracker.BuildReport(60, 25, Now).Protective;

        rows.Select(r => r.Scope).Should().Equal("auth_failure", "anonymous");
        rows.Should().OnlyContain(r => r.Checked == 0 && r.Charged == 0 && r.Refused == 0 && r.LastDecisionUtc == null);
    }

    [Fact]
    public void Protective_AuthFailure_CountsChecksChargesAndRefusals()
    {
        var (tracker, _) = Create();

        tracker.RecordAuthFailure(RateLimitAuthFailureStep.Checked, 30);
        tracker.RecordAuthFailure(RateLimitAuthFailureStep.Checked, 30);
        tracker.RecordAuthFailure(RateLimitAuthFailureStep.Charged, 30);
        tracker.RecordAuthFailure(RateLimitAuthFailureStep.Refused, 30);

        var row = tracker.BuildReport(60, 25, Now).Protective.Single(r => r.Scope == "auth_failure");
        row.Checked.Should().Be(3, "a charge follows a check for the same request and is not a second check");
        row.Charged.Should().Be(1);
        row.Refused.Should().Be(1);
        row.EnforcedRpm.Should().Be(30);
        row.LimitId.Should().Be("auth_failure:*");
    }

    /// <summary>The anonymous rows are reserved: a full limits section cannot make them read zero.</summary>
    [Fact]
    public void Protective_Anonymous_IsCountedOutsideTheKeyCeiling_AndNotListedAsALimit()
    {
        var (tracker, _) = Create(maxKeys: 10);
        for (var i = 0; i < 10; i++)
        {
            tracker.RecordRateStage([Rule(RateLimitScope.ApiKey, "k:" + i, "api_key:k" + i)], RateLimitStageOutcome.Charged);
        }

        var anonymous = Rule(RateLimitScope.Tenant, "t:anon:10.0.0.0", RateLimitLimitIds.Anonymous, rpm: 20);
        tracker.RecordRateStage([anonymous], RateLimitStageOutcome.Charged);
        tracker.RecordRateStage([anonymous], RateLimitStageOutcome.Refused, "t:anon:10.0.0.0");

        var report = tracker.BuildReport(60, 25, Now);
        var row = report.Protective.Single(r => r.Scope == "anonymous");
        row.Checked.Should().Be(2);
        row.Charged.Should().Be(1);
        row.Refused.Should().Be(1);
        row.EnforcedRpm.Should().Be(20);
        report.Limits.Should().NotContain(l => l.LimitId == "anonymous:*");
        report.Tracker.IsSaturated.Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsLimitsAndProtective()
    {
        var (tracker, _) = Create();
        tracker.RecordRateStage([Rule(RateLimitScope.Tenant, "t:a", "tenant:a")], RateLimitStageOutcome.Charged);
        tracker.RecordAuthFailure(RateLimitAuthFailureStep.Refused, 30);

        tracker.Reset();

        var report = tracker.BuildReport(60, 25, Now);
        report.Limits.Should().BeEmpty();
        report.Protective.Should().OnlyContain(r => r.Checked == 0 && r.EnforcedRpm == 0);
    }

    // --- Series ---------------------------------------------------------------------------------

    [Fact]
    public void Series_WithNoTraffic_IsContiguousZerosNotAnEmptyList()
    {
        var (tracker, _) = Create();

        var series = tracker.BuildSeries(10, 1, null, false, Now)!;

        series.Subject.Should().Be("gateway");
        series.BucketMinutes.Should().Be(1);
        series.Points.Should().HaveCount(10);
        series.Points.Should().OnlyContain(p => p.Decisions == 0);
        series.Points[^1].StartUtc.Should().Be(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        series.ToUtc.Should().Be(new DateTimeOffset(2026, 9, 21, 12, 1, 0, TimeSpan.Zero));
        series.Points.Zip(series.Points.Skip(1)).Should().OnlyContain(pair => pair.Second.StartUtc - pair.First.StartUtc == TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Series_SplitsRefusalsAndLeavesGapsAsZero()
    {
        var (tracker, time) = Create();
        Admit(tracker, "acme");
        Admit(tracker, "acme");
        time.Advance(TimeSpan.FromMinutes(3));
        Refuse(tracker, "acme", RateLimitControl.Rate);
        Refuse(tracker, "acme", RateLimitControl.Concurrency);

        var points = tracker.BuildSeries(5, 1, null, false, time.GetUtcNow())!.Points;

        points.Select(p => p.Decisions).Should().Equal(0, 2, 0, 0, 2);
        points[1].Admitted.Should().Be(2);
        points[4].RefusedByRate.Should().Be(1);
        points[4].RefusedByStreams.Should().Be(1);
    }

    [Fact]
    public void Series_AggregatesIntoEpochAlignedBuckets()
    {
        var (tracker, time) = Create();
        Admit(tracker, "acme");                       // 12:00
        time.Advance(TimeSpan.FromMinutes(4));
        Admit(tracker, "acme");                       // 12:04
        time.Advance(TimeSpan.FromMinutes(1));
        Admit(tracker, "acme");                       // 12:05

        var series = tracker.BuildSeries(10, 5, null, false, time.GetUtcNow())!;

        // Ten minutes back from 12:05 is 11:56, which sits in the 11:55 bucket: whole buckets only,
        // so the range is rounded outwards to cover what was asked for.
        series.Points.Select(p => p.StartUtc.Minute).Should().Equal(55, 0, 5);
        series.Points.Select(p => p.Decisions).Should().Equal(0, 2, 1);
        series.FromUtc.Should().Be(new DateTimeOffset(2026, 9, 21, 11, 55, 0, TimeSpan.Zero));
    }

    /// <summary>Before counting began there is nothing to report, and a zero there must not read as "quiet".</summary>
    [Fact]
    public void Series_MarksBucketsBeforeTrackingBeganAsNotCovered()
    {
        var (tracker, time) = Create();
        time.Advance(TimeSpan.FromMinutes(2));

        var points = tracker.BuildSeries(5, 1, null, false, time.GetUtcNow())!.Points;

        points.Select(p => p.Covered).Should().Equal(false, false, true, true, true);
    }

    [Fact]
    public void Series_AfterAReset_IsNotCoveredBeforeTheReset()
    {
        var (tracker, time) = Create();
        Admit(tracker, "acme");
        time.Advance(TimeSpan.FromMinutes(3));
        tracker.Reset();

        var points = tracker.BuildSeries(4, 1, null, false, time.GetUtcNow())!.Points;

        points.Should().OnlyContain(p => p.Decisions == 0);
        points.Select(p => p.Covered).Should().Equal(false, false, false, true);
    }

    [Fact]
    public void Series_ClampsToWhatTheRingHolds()
    {
        var (tracker, _) = Create();

        tracker.BuildSeries(10_000, 1, null, false, Now)!.Points.Should().HaveCount(180);
        tracker.BuildSeries(10_000, 60, null, false, Now)!.Points.Count.Should().BeLessThanOrEqualTo(4);
    }

    [Fact]
    public void Series_ForALimit_UsesThatLimitsCounters_AndIsNullForAnUnknownOne()
    {
        var (tracker, _) = Create();
        var rule = Rule(RateLimitScope.Tenant, "t:acme", "tenant:acme");
        tracker.RecordRateStage([rule], RateLimitStageOutcome.Charged);
        tracker.RecordRateStage([rule], RateLimitStageOutcome.Refused, "t:acme");

        var series = tracker.BuildSeries(3, 1, "Tenant:ACME", false, Now)!;

        series.Subject.Should().Be("limit");
        series.LimitId.Should().Be("tenant:acme");
        series.Points[^1].Decisions.Should().Be(2);
        series.Points[^1].Admitted.Should().Be(1);
        series.Points[^1].RefusedByRate.Should().Be(1);
        tracker.BuildSeries(3, 1, "tenant:nobody", false, Now).Should().BeNull();
        tracker.BuildSeries(3, 1, "auth_failure:*", false, Now).Should().NotBeNull("the protective rings always exist");
    }

    // --- Helpers --------------------------------------------------------------------------------

    private static (RateLimitUsageTracker Tracker, MutableTimeProvider Time) Create(int maxKeys = 500)
    {
        var time = new MutableTimeProvider(Now);
        return (new RateLimitUsageTracker(Options.Create(new RateLimitingOptions { UsageReportMaxKeys = maxKeys }), timeProvider: time), time);
    }

    private static RateLimitRule Rule(RateLimitScope scope, string partition, string limitId, int rpm = 100) =>
        new(scope, partition, new RateLimitPolicy(rpm, 0, 0)) { LimitId = limitId, StreamLimitId = limitId };

    private static void Admit(RateLimitUsageTracker tracker, string tenant) =>
        tracker.Record(new RateLimitUsageEvent(tenant, null, null, true, RateLimitScope.Tenant, RateLimitControl.Rate, 100, 100));

    private static void Refuse(RateLimitUsageTracker tracker, string tenant, RateLimitControl control) =>
        tracker.Record(new RateLimitUsageEvent(tenant, null, null, false, RateLimitScope.Tenant, control, 100, 100));

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
