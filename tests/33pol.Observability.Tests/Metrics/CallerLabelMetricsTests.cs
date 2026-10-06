using System.Diagnostics.Metrics;
using Pol33.Core.Observability;
using Pol33.Core.RateLimiting;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;
using Pol33.Observability.Tracking;

namespace Pol33.Observability.Tests.Metrics;

/// <summary>
/// The series that have to name a caller. Before these labels, Prometheus could say a model was
/// refusing or failing but not for whom — on 2026-09-23 one key had 3,727 of 4,398 requests refused
/// in an hour and no series could show it.
/// </summary>
/// <remarks>
/// The meter is process-wide and other tests record on it concurrently, so each test uses a model
/// id of its own and reads only the measurements carrying it.
/// </remarks>
public sealed class CallerLabelMetricsTests
{
    private static readonly MetricCaller Fanus = new("fanus", "Fanus-MMT-Campaign");

    [Fact]
    public void CompletedRequest_NamesTheCallerOnRequestsAndDuration()
    {
        using var recorded = Recording.Of("caller-ok");
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState());

        using (var scope = tracker.BeginInferenceRequest("caller-ok", isStreaming: false, tenantId: "t1", Fanus))
        {
            scope.SetOutcome(true);
        }

        recorded.Single("gateway_inference_requests_total").Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["model"] = "caller-ok",
            ["status"] = "success",
            ["tenant"] = "fanus",
            ["key"] = "Fanus-MMT-Campaign",
        });
        recorded.Single("gateway_inference_duration_seconds").Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["model"] = "caller-ok",
            ["tenant"] = "fanus",
            ["key"] = "Fanus-MMT-Campaign",
        });
    }

    [Fact]
    public void FailedRequest_NamesTheCallerOnErrors()
    {
        using var recorded = Recording.Of("caller-failed");
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState());

        using (var scope = tracker.BeginInferenceRequest("caller-failed", isStreaming: false, tenantId: "t1", Fanus))
        {
            scope.SetOutcome(false, "upstream_error");
        }

        recorded.Single("gateway_inference_errors_total").Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["model"] = "caller-failed",
            ["code"] = "upstream_error",
            ["tenant"] = "fanus",
            ["key"] = "Fanus-MMT-Campaign",
        });
    }

    [Fact]
    public void CanceledRequest_NamesTheCaller()
    {
        using var recorded = Recording.Of("caller-canceled");
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState());

        using (var scope = tracker.BeginInferenceRequest("caller-canceled", isStreaming: true, tenantId: "t1", Fanus))
        {
            scope.SetClientCanceled();
        }

        var tags = recorded.Single("gateway_inference_requests_total");
        tags["status"].Should().Be("canceled");
        tags["key"].Should().Be("Fanus-MMT-Campaign");
    }

    [Fact]
    public void RejectedRequest_NamesTheCallerOnRequestsAndErrors()
    {
        using var recorded = Recording.Of("caller-rejected");
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState());

        tracker.RecordRejectedRequest("caller-rejected", "circuit_open", Fanus);

        recorded.Single("gateway_inference_requests_total")["tenant"].Should().Be("fanus");
        var error = recorded.Single("gateway_inference_errors_total");
        error["code"].Should().Be("circuit_open");
        error["key"].Should().Be("Fanus-MMT-Campaign");
    }

    /// <summary>
    /// A call site that names nobody must still produce the two labels. A series that sometimes has
    /// a label and sometimes does not is two series to PromQL, and <c>sum by (tenant)</c> would
    /// split one caller's traffic across a named row and a blank one.
    /// </summary>
    [Fact]
    public void RequestWithNoCaller_IsAnonymous_NotUnlabelled()
    {
        using var recorded = Recording.Of("caller-none");
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState());

        tracker.BeginInferenceRequest("caller-none", isStreaming: false).Dispose();

        var tags = recorded.Single("gateway_inference_requests_total");
        tags["tenant"].Should().Be("anonymous");
        tags["key"].Should().Be("(none)");
    }

    /// <summary>
    /// The gauges are level counters that are added to on entry and subtracted from on exit. A
    /// caller label there would have to match on both sides forever, and buys a series per caller
    /// that reads zero almost all the time.
    /// </summary>
    [Fact]
    public void ActiveRequestGauges_StayOnTheModelAlone()
    {
        using var recorded = Recording.Of("caller-gauge");
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState());

        tracker.BeginInferenceRequest("caller-gauge", isStreaming: true, tenantId: "t1", Fanus).Dispose();

        recorded.All("gateway_active_requests").Should().OnlyContain(tags => !tags.ContainsKey("tenant"));
        recorded.All("gateway_active_streams").Should().OnlyContain(tags => !tags.ContainsKey("key"));
    }

    [Fact]
    public void RequestsPastTheBudget_AreCountedUnderOther_NotDropped()
    {
        using var recorded = Recording.Of("caller-overflow");
        var budget = new MetricCallerBudget(maxCallers: 10);
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState(), budget);

        for (var i = 0; i < 12; i++)
        {
            tracker.BeginInferenceRequest(
                "caller-overflow", isStreaming: false, tenantId: "t1", new MetricCaller("fanus", "key-" + i)).Dispose();
        }

        var keys = recorded.All("gateway_inference_requests_total").Select(tags => tags["key"]).ToList();
        keys.Should().HaveCount(12);
        keys.Count(k => k == "other").Should().Be(2);
        recorded.All("gateway_inference_requests_total").Should().OnlyContain(tags => tags["tenant"] == "fanus");
    }

    [Fact]
    public void TokenUsage_NamesTheCallerOnEveryDirection()
    {
        using var recorded = Recording.Of("caller-tokens");
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());

        collector.RecordTokenUsage("caller-tokens", promptTokens: 10, completionTokens: 5, Fanus);

        var series = recorded.All("gateway_tokens_total");
        series.Select(tags => tags["direction"]).Should().BeEquivalentTo(["input", "output", "total"]);
        series.Should().OnlyContain(tags => tags["tenant"] == "fanus" && tags["key"] == "Fanus-MMT-Campaign");
    }

    [Fact]
    public void RateLimitRefusal_NamesTheCallerAndTheLimitThatRefused()
    {
        using var recorded = Recording.Of("caller-refused");
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());

        collector.RecordRateLimitDecision(
            Fanus, "caller-refused", RateLimitScope.ApiKey, RateLimitControl.Rate, admitted: false);

        recorded.Single("gateway_rate_limit_decisions_total").Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["tenant"] = "fanus",
            ["key"] = "Fanus-MMT-Campaign",
            ["model"] = "caller-refused",
            ["scope"] = "api_key",
            ["control"] = "rate",
            ["outcome"] = "refused",
        });
    }

    /// <summary>
    /// An admission is not attributed to a scope. The limiter knows only the tightest one, and that
    /// changes from request to request with whichever bucket happens to be lowest — labelling it
    /// would spread one caller's admissions over every scope for no question anyone asks.
    /// </summary>
    [Fact]
    public void RateLimitAdmission_CarriesNoScope()
    {
        using var recorded = Recording.Of("caller-admitted");
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());

        collector.RecordRateLimitDecision(
            Fanus, "caller-admitted", RateLimitScope.Tenant, RateLimitControl.Rate, admitted: true);

        var tags = recorded.Single("gateway_rate_limit_decisions_total");
        tags["outcome"].Should().Be("admitted");
        tags["scope"].Should().Be("none");
    }

    [Fact]
    public void StreamCapRefusal_IsAConcurrencyDecision()
    {
        using var recorded = Recording.Of("caller-stream");
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());

        collector.RecordRateLimitDecision(
            Fanus, "caller-stream", RateLimitScope.Tenant, RateLimitControl.Concurrency, admitted: false);

        var tags = recorded.Single("gateway_rate_limit_decisions_total");
        tags["control"].Should().Be("concurrency");
        tags["scope"].Should().Be("tenant");
    }

    /// <summary>
    /// Identity scopes are decided before the body is read, so there is no model to name. The label
    /// is still present, for the same reason an absent caller is <c>anonymous</c>.
    /// </summary>
    [Fact]
    public void DecisionBeforeTheBodyIsParsed_HasAnUnknownModel()
    {
        var caller = new MetricCaller("caller-no-model-tenant", "k");
        using var recorded = Recording.Where(tags => tags.GetValueOrDefault("tenant") == caller.Tenant);
        var collector = new GatewayMetricsCollector(new GatewayRuntimeState());

        collector.RecordRateLimitDecision(caller, modelId: null, RateLimitScope.Tenant, RateLimitControl.Rate, admitted: false);

        recorded.Single("gateway_rate_limit_decisions_total")["model"].Should().Be("unknown");
    }

    /// <summary>
    /// The exporter raises the cardinality limit for exactly this list, so an instrument that gains
    /// caller labels without being added to it would silently keep the SDK's 2,000-point default.
    /// </summary>
    [Fact]
    public void EveryCallerLabelledInstrument_IsListedForTheExporter()
    {
        GatewayMeters.CallerLabelledInstruments.Should().BeEquivalentTo(
        [
            GatewayMeters.InferenceRequests.Name,
            GatewayMeters.InferenceErrors.Name,
            GatewayMeters.InferenceDuration.Name,
            GatewayMeters.TokensTotal.Name,
            GatewayMeters.RateLimitDecisions.Name,
        ]);
    }

    private sealed class Recording : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string Instrument, Dictionary<string, string> Tags)> _measurements = [];
        private readonly Func<Dictionary<string, string>, bool> _mine;

        private Recording(Func<Dictionary<string, string>, bool> mine)
        {
            _mine = mine;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == GatewayMeters.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Add(instrument, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Add(instrument, tags));
            _listener.Start();
        }

        public static Recording Of(string modelId) => Where(tags => tags.GetValueOrDefault("model") == modelId);

        public static Recording Where(Func<Dictionary<string, string>, bool> mine) => new(mine);

        public Dictionary<string, string> Single(string instrument) =>
            All(instrument).Should().ContainSingle().Subject;

        public List<Dictionary<string, string>> All(string instrument)
        {
            lock (_measurements)
            {
                return _measurements.Where(m => m.Instrument == instrument).Select(m => m.Tags).ToList();
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var captured = new Dictionary<string, string>(tags.Length);
            foreach (var tag in tags)
            {
                captured[tag.Key] = tag.Value?.ToString() ?? string.Empty;
            }

            if (!_mine(captured))
            {
                return;
            }

            lock (_measurements)
            {
                _measurements.Add((instrument.Name, captured));
            }
        }
    }
}
