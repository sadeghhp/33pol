using System.Diagnostics.Metrics;
using Pol33.Core.Observability;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;
using Pol33.Observability.Tracking;

namespace Pol33.Observability.Tests.Metrics;

/// <summary>
/// Open streams per caller. A stream cap set below a key's normal peak refuses traffic that is
/// admitted today, and before these gauges nothing recorded that peak.
/// </summary>
public sealed class CallerOpenStreamsTests
{
    private static readonly MetricCaller Fanus = new("fanus", "Fanus-MMT-Campaign");
    private static readonly MetricCaller Easy = new("easy", "easy-prod-main");

    [Fact]
    public void OpenStreams_AreCountedPerCaller_AndReleasedWhenTheStreamEnds()
    {
        var streams = new CallerOpenStreams();
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState(), openStreams: streams);

        var first = tracker.BeginInferenceRequest("m", isStreaming: true, tenantId: "t1", Fanus);
        var second = tracker.BeginInferenceRequest("m", isStreaming: true, tenantId: "t1", Fanus);
        var other = tracker.BeginInferenceRequest("m", isStreaming: true, tenantId: "t2", Easy);

        Read(streams.ObserveOpen()).Should().BeEquivalentTo(new Dictionary<MetricCaller, long>
        {
            [Fanus] = 2,
            [Easy] = 1,
        });

        first.Dispose();
        second.Dispose();
        other.Dispose();

        // The series stays, reading zero: "no streams open" is not "never seen".
        Read(streams.ObserveOpen()).Should().BeEquivalentTo(new Dictionary<MetricCaller, long>
        {
            [Fanus] = 0,
            [Easy] = 0,
        });
    }

    [Fact]
    public void Peak_KeepsTheMostOpenAtOnce_AfterTheStreamsHaveEnded()
    {
        var streams = new CallerOpenStreams();
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState(), openStreams: streams);

        var open = Enumerable.Range(0, 3)
            .Select(_ => tracker.BeginInferenceRequest("m", isStreaming: true, tenantId: "t1", Fanus))
            .ToList();
        open.ForEach(scope => scope.Dispose());
        using (tracker.BeginInferenceRequest("m", isStreaming: true, tenantId: "t1", Fanus))
        {
        }

        Read(streams.ObservePeak())[Fanus].Should().Be(3);
        Read(streams.ObserveOpen())[Fanus].Should().Be(0);
    }

    [Fact]
    public void ARequestThatDoesNotStream_IsNotAnOpenStream()
    {
        var streams = new CallerOpenStreams();
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState(), openStreams: streams);

        using (tracker.BeginInferenceRequest("m", isStreaming: false, tenantId: "t1", Fanus))
        {
            Read(streams.ObserveOpen()).Should().BeEmpty();
        }
    }

    /// <summary>Disposing a scope twice must not hand back a slot it never took.</summary>
    [Fact]
    public void DisposingAScopeTwice_ReleasesTheStreamOnce()
    {
        var streams = new CallerOpenStreams();
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState(), openStreams: streams);

        var kept = tracker.BeginInferenceRequest("m", isStreaming: true, tenantId: "t1", Fanus);
        var ended = tracker.BeginInferenceRequest("m", isStreaming: true, tenantId: "t1", Fanus);
        ended.Dispose();
        ended.Dispose();

        Read(streams.ObserveOpen())[Fanus].Should().Be(1);
        kept.Dispose();
    }

    /// <summary>
    /// The gauges are bounded by the same budget as every other caller series: a caller past it is
    /// counted under its tenant's <c>other</c>, not given a series of its own.
    /// </summary>
    [Fact]
    public void ACallerPastTheBudget_IsCountedUnderOther()
    {
        var streams = new CallerOpenStreams();
        var budget = new MetricCallerBudget(maxCallers: 10);
        var tracker = new GatewayRequestTracker(new GatewayRuntimeState(), budget, streams);

        var scopes = Enumerable.Range(0, 12)
            .Select(i => tracker.BeginInferenceRequest("m", isStreaming: true, tenantId: "t1", new MetricCaller("fanus", $"key-{i}")))
            .ToList();

        var open = Read(streams.ObserveOpen());
        open.Should().HaveCount(11);
        open[new MetricCaller("fanus", MetricCaller.Overflow)].Should().Be(2);

        scopes.ForEach(scope => scope.Dispose());
    }

    [Fact]
    public async Task Exporter_PublishesBothGaugesWithTenantAndKey()
    {
        var streams = new CallerOpenStreams();
        var caller = new MetricCaller("exporter-tenant", "exporter-key");
        streams.For(caller).StreamStarted();

        var seen = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == GatewayMeters.MeterName &&
                instrument.Name is GatewayCallerStreamsMetricsExporter.OpenStreamsName
                    or GatewayCallerStreamsMetricsExporter.PeakOpenStreamsName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? tenant = null, key = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "tenant") tenant = tag.Value?.ToString();
                if (tag.Key == "key") key = tag.Value?.ToString();
            }

            // Other tests may have started an exporter of their own on the process-wide meter.
            if (tenant == caller.Tenant && key == caller.Key)
            {
                seen[instrument.Name] = value;
            }
        });

        await new GatewayCallerStreamsMetricsExporter(streams).StartAsync(CancellationToken.None);
        listener.Start();
        listener.RecordObservableInstruments();

        seen.Should().BeEquivalentTo(new Dictionary<string, long>
        {
            [GatewayCallerStreamsMetricsExporter.OpenStreamsName] = 1,
            [GatewayCallerStreamsMetricsExporter.PeakOpenStreamsName] = 1,
        });
    }

    private static Dictionary<MetricCaller, long> Read(IEnumerable<Measurement<long>> measurements)
    {
        var read = new Dictionary<MetricCaller, long>();
        foreach (var measurement in measurements)
        {
            string tenant = "", key = "";
            foreach (var tag in measurement.Tags)
            {
                if (tag.Key == "tenant") tenant = tag.Value?.ToString() ?? "";
                if (tag.Key == "key") key = tag.Value?.ToString() ?? "";
            }

            read[new MetricCaller(tenant, key)] = measurement.Value;
        }

        return read;
    }
}
