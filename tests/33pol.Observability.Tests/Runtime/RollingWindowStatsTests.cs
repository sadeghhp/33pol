using Pol33.Core.Models.Overview;
using Pol33.Core.Observability;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Tests.Runtime;

public sealed class RollingWindowStatsTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResetErrors_ZeroesErrorsAndRejectionsButKeepsRequests()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);
        stats.RecordCompletion("gpt-local", 100, success: true, isStreaming: false);
        stats.RecordCompletion("gpt-local", 300, success: false, isStreaming: false);
        stats.RecordRejection("gpt-local", RejectionReason.Bulkhead);

        stats.ResetErrors();

        var window = stats.GetWindow(TimeSpan.FromMinutes(5), "5m");
        window.Requests.Should().Be(3);
        window.Errors.Should().Be(0);
        window.ErrorRate.Should().Be(0);
        window.PerModel[0].Errors.Should().Be(0);
        window.RejectionsByReason.Should().BeEmpty();
    }

    [Fact]
    public void GetWindow_AfterCompletions_ReportsCountsAndErrorRate()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);

        stats.RecordCompletion("gpt-local", 100, success: true, isStreaming: false);
        stats.RecordCompletion("gpt-local", 200, success: true, isStreaming: true);
        stats.RecordCompletion("gpt-local", 300, success: false, isStreaming: false);
        stats.RecordCompletion("embed", 50, success: true, isStreaming: false);

        var window = stats.GetWindow(TimeSpan.FromMinutes(5), "5m");

        window.Window.Should().Be("5m");
        window.WindowSeconds.Should().Be(300);
        window.Requests.Should().Be(4);
        window.Errors.Should().Be(1);
        window.ErrorRate.Should().BeApproximately(0.25, 1e-9);
        window.RequestsPerSecond.Should().BeApproximately(4d / 300, 1e-9);
        window.LatencyAvgMs.Should().BeApproximately(162.5, 1e-9);
        window.PerModel.Should().HaveCount(2);
        window.PerModel[0].ModelId.Should().Be("gpt-local");
        window.PerModel[0].Requests.Should().Be(3);
        window.PerModel[0].Errors.Should().Be(1);
        window.PerModel[1].ModelId.Should().Be("embed");
    }

    [Fact]
    public void GetWindow_OneMinute_UsesSecondBucketsSoOldSecondsExpire()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);

        stats.RecordCompletion("m", 100, success: true, isStreaming: false);
        clock.Advance(TimeSpan.FromSeconds(30));
        stats.RecordCompletion("m", 100, success: true, isStreaming: false);
        clock.Advance(TimeSpan.FromSeconds(45));

        stats.GetWindow(TimeSpan.FromMinutes(1)).Requests.Should().Be(1, "the first sample is 75 s old");
        stats.GetWindow(TimeSpan.FromMinutes(5)).Requests.Should().Be(2);
    }

    [Fact]
    public void GetWindow_OlderThanWindow_IsExcluded()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);

        stats.RecordCompletion("m", 100, success: true, isStreaming: false);
        clock.Advance(TimeSpan.FromHours(2));
        stats.RecordCompletion("m", 100, success: true, isStreaming: false);

        stats.GetWindow(TimeSpan.FromHours(1)).Requests.Should().Be(1);
        stats.GetWindow(TimeSpan.FromHours(24)).Requests.Should().Be(2);

        clock.Advance(TimeSpan.FromHours(23));
        stats.GetWindow(TimeSpan.FromHours(24)).Requests.Should().Be(1, "the first sample is now 25 h old");
    }

    [Fact]
    public void GetWindow_RingWrapsAfterADay_DoesNotResurrectStaleBuckets()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);

        stats.RecordCompletion("m", 100, success: true, isStreaming: false);
        clock.Advance(TimeSpan.FromHours(24));
        stats.RecordCompletion("m", 100, success: true, isStreaming: false);

        // The slot the first sample used has been reassigned to the current minute.
        stats.GetWindow(TimeSpan.FromHours(24)).Requests.Should().Be(1);
    }

    [Fact]
    public void GetWindow_Percentiles_FallInsideExpectedBins()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);

        for (var i = 0; i < 95; i++)
        {
            stats.RecordCompletion("m", 80, success: true, isStreaming: false); // bin (50, 100]
        }

        for (var i = 0; i < 5; i++)
        {
            stats.RecordCompletion("m", 4_000, success: true, isStreaming: false); // bin (2000, 5000]
        }

        var window = stats.GetWindow(TimeSpan.FromMinutes(5));

        window.LatencyP50Ms.Should().BeInRange(50, 100);
        window.LatencyP95Ms.Should().BeInRange(50, 100);
        window.LatencyP99Ms.Should().BeInRange(2_000, 5_000);
        window.PerModel.Single().LatencyP95Ms.Should().BeInRange(50, 100);
    }

    [Fact]
    public void GetWindow_TimeToFirstToken_IsNullWithoutSamplesAndPopulatedWithThem()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);

        stats.RecordCompletion("m", 500, success: true, isStreaming: false);
        stats.GetWindow(TimeSpan.FromMinutes(5)).TtftP95Ms.Should().BeNull();

        stats.RecordTimeToFirstToken("m", 150);
        var window = stats.GetWindow(TimeSpan.FromMinutes(5));

        window.TtftSamples.Should().Be(1);
        window.TtftP95Ms.Should().NotBeNull().And.BeInRange(100, 250);
        window.PerModel.Single().TtftP95Ms.Should().NotBeNull();
    }

    [Fact]
    public void RecordUsage_AccumulatesTokensAndOnlyPricedCost()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);

        stats.RecordUsage("m", 100, 50, pricedCost: null);
        stats.RecordUsage("m", 10, 5, pricedCost: 0.25m);

        var window = stats.GetWindow(TimeSpan.FromMinutes(5));

        window.PromptTokens.Should().Be(110);
        window.CompletionTokens.Should().Be(55);
        window.PricedCost.Should().Be(0.25m);
        window.PricedRequests.Should().Be(1);
        window.PerModel.Single().PricedCost.Should().Be(0.25m);
    }

    [Fact]
    public void RecordRejection_CountsAsFailedRequestAndByReason()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);

        stats.RecordRejection(null, RejectionReason.RateLimit);
        stats.RecordRejection("m", RejectionReason.Budget);
        stats.RecordRejection("m", RejectionReason.Budget);

        var window = stats.GetWindow(TimeSpan.FromMinutes(5));

        window.Requests.Should().Be(3);
        window.Errors.Should().Be(3);
        window.RejectionsByReason.Should().Equal(new Dictionary<string, long>
        {
            ["rate_limit"] = 1,
            ["budget"] = 2,
        });
        window.LatencyAvgMs.Should().Be(0, "rejections carry no latency");
    }

    [Fact]
    public void GetSeries_ReturnsRequestedMinutesAlignedToMinuteEndingNow()
    {
        var clock = new FakeTimeProvider(Start.AddSeconds(30));
        var stats = new RollingWindowStats(clock);

        stats.RecordCompletion("m", 100, success: true, isStreaming: false);
        stats.SampleInFlight(3);
        stats.SampleInFlight(1);
        clock.Advance(TimeSpan.FromMinutes(1));
        stats.RecordCompletion("m", 100, success: false, isStreaming: false);

        var series = stats.GetSeries(60);

        series.StepSeconds.Should().Be(60);
        series.Points.Should().HaveCount(60);
        series.StartUtc.Should().Be(Start.AddMinutes(-58));
        series.Points[^2].Requests.Should().Be(1);
        series.Points[^2].InFlight.Should().Be(3, "the series keeps the per-minute peak");
        series.Points[^1].Errors.Should().Be(1);
        series.Points[^1].T.Should().Be(Start.AddMinutes(1));
        series.Points[0].Requests.Should().Be(0, "untouched buckets are zero, not missing");
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var clock = new FakeTimeProvider(Start);
        var stats = new RollingWindowStats(clock);
        stats.RecordCompletion("m", 100, success: true, isStreaming: false);
        stats.RecordUsage("m", 10, 5, 1m);

        stats.Reset();

        var window = stats.GetWindow(TimeSpan.FromHours(24));
        window.Requests.Should().Be(0);
        window.PromptTokens.Should().Be(0);
        window.PerModel.Should().BeEmpty();
    }

    [Fact]
    public void MaxTrackedModels_PastTheCap_StillCountsGatewayWide()
    {
        var stats = new RollingWindowStats(new FakeTimeProvider(Start)) { MaxTrackedModels = 1 };

        stats.RecordCompletion("first", 100, success: true, isStreaming: false);
        stats.RecordCompletion("second", 100, success: true, isStreaming: false);

        var window = stats.GetWindow(TimeSpan.FromMinutes(5));
        window.Requests.Should().Be(2);
        window.PerModel.Should().ContainSingle(m => m.ModelId == "first");
    }

    [Fact]
    public void RetainOnly_DropsRingsForUnknownModels()
    {
        var stats = new RollingWindowStats(new FakeTimeProvider(Start));
        stats.RecordCompletion("keep", 100, success: true, isStreaming: false);
        stats.RecordCompletion("drop", 100, success: true, isStreaming: false);

        stats.RetainOnly(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "keep" });

        stats.GetWindow(TimeSpan.FromMinutes(5)).PerModel.Should().ContainSingle(m => m.ModelId == "keep");
    }

    [Fact]
    public void Disabled_RecordsNothing()
    {
        var stats = new RollingWindowStats(new FakeTimeProvider(Start)) { Enabled = false };

        stats.RecordCompletion("m", 100, success: true, isStreaming: false);

        stats.GetWindow(TimeSpan.FromMinutes(5)).Requests.Should().Be(0);
    }

    [Fact]
    public void RecordCompletion_Concurrent_DoesNotLoseCounts()
    {
        var stats = new RollingWindowStats(new FakeTimeProvider(Start));

        Parallel.For(0, 10_000, i => stats.RecordCompletion(i % 2 == 0 ? "a" : "b", i % 1000, success: i % 10 != 0, isStreaming: false));

        var window = stats.GetWindow(TimeSpan.FromMinutes(5));
        window.Requests.Should().Be(10_000);
        window.Errors.Should().Be(1_000);
        window.PerModel.Sum(m => m.Requests).Should().Be(10_000);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0)]
    [InlineData(50.1, 1)]
    [InlineData(600_000, 13)]
    [InlineData(600_001, 14)]
    public void BinIndex_IsUpperInclusive(double value, int expected)
    {
        RollingWindowStats.BinIndex(LatencyHistogramBoundaries.DurationMs, value).Should().Be(expected);
    }

    [Fact]
    public void Percentile_EmptyHistogram_IsZero()
    {
        RollingWindowStats.Percentile(new long[15], LatencyHistogramBoundaries.DurationMs, 0.95).Should().Be(0);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void RecordRejection_ReasonOnly_DoesNotCountAsARequest()
    {
        var stats = new RollingWindowStats(new FakeTimeProvider(Start));

        stats.RecordRejection(null, RejectionReason.RateLimit, countAsFailedRequest: false);
        stats.RecordRejection("m", null, countAsFailedRequest: true);

        var window = stats.GetWindow(TimeSpan.FromMinutes(5));
        window.Requests.Should().Be(1, "only the admission rejection counts as a request");
        window.Errors.Should().Be(1);
        window.RejectionsByReason.Should().Equal(new Dictionary<string, long> { ["rate_limit"] = 1 });
    }

}
