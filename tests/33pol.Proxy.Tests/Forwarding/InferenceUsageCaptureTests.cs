using NSubstitute;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class InferenceUsageCaptureTests
{
    [Fact]
    public void CaptureFromJsonBody_EnqueuesUsageEvent()
    {
        var recorder = Substitute.For<IUsageRecorder>();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var capture = new InferenceUsageCapture(
            recorder,
            metrics,
            "canonical",
            "req-1",
            DateTimeOffset.UtcNow.AddSeconds(-1),
            tenant: null);

        var body = """{"usage":{"prompt_tokens":2,"completion_tokens":3}}"""u8.ToArray();
        capture.CaptureFromJsonBody(body);

        recorder.Received(1).Enqueue(Arg.Is<UsageEvent>(e =>
            e.RequestId == "req-1" &&
            e.ModelId == "canonical" &&
            e.PromptTokens == 2 &&
            e.CompletionTokens == 3));
    }

    [Fact]
    public void CaptureFromJsonBody_TotalTokensOnly_EnqueuesUsageEvent()
    {
        var recorder = Substitute.For<IUsageRecorder>();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var capture = new InferenceUsageCapture(
            recorder,
            metrics,
            "reranker",
            "req-rerank",
            DateTimeOffset.UtcNow.AddSeconds(-1),
            tenant: null);

        var body = """{"usage":{"total_tokens":56}}"""u8.ToArray();
        capture.CaptureFromJsonBody(body);

        // Behaviour change: the total is no longer folded into PromptTokens (which priced it at the
        // input rate and under-billed). It is carried as TotalOnly so pricing applies the explicit
        // conservative policy, and the approximation is metered.
        recorder.Received(1).Enqueue(Arg.Is<UsageEvent>(e =>
            e.RequestId == "req-rerank" &&
            e.ModelId == "reranker" &&
            e.TokenSource == UsageTokenSource.TotalOnly &&
            e.TotalTokens == 56 &&
            e.PromptTokens == 0 &&
            e.CompletionTokens == 0));
        metrics.Received(1).RecordUnsplitUsage("reranker");
        metrics.DidNotReceive().RecordUsageParseFailure(Arg.Any<string>());
    }

    [Fact]
    public void CaptureFromJsonBody_WhenMissingUsage_RecordsParseFailure()
    {
        var recorder = Substitute.For<IUsageRecorder>();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var capture = new InferenceUsageCapture(
            recorder,
            metrics,
            "canonical",
            "req-2",
            DateTimeOffset.UtcNow,
            tenant: null);

        capture.CaptureFromJsonBody("""{"id":"x"}"""u8.ToArray());

        recorder.DidNotReceive().Enqueue(Arg.Any<UsageEvent>());
        metrics.Received(1).RecordUsageParseFailure("canonical");
    }

    /// <summary>
    /// A stream cut short before its terminal usage frame must be billed from an estimate. Recording
    /// nothing meant a client that disconnects just before completion got free inference while the
    /// upstream had already generated (and charged for) the tokens.
    /// </summary>
    [Fact]
    public void CaptureFromSseText_TruncatedStreamWithOutput_EnqueuesAnEstimate()
    {
        var recorder = Substitute.For<IUsageRecorder>();

        // Accepted by the queue. HasEnqueuedUsage tracks acceptance rather than the attempt: a
        // saturated queue drops silently, and treating a dropped event as persisted left the
        // router's budget reservation held for its whole TTL.
        recorder.Enqueue(Arg.Any<UsageEvent>()).Returns(true);

        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var capture = new InferenceUsageCapture(
            recorder,
            metrics,
            "gpt-4o",
            "req-cut",
            DateTimeOffset.UtcNow.AddSeconds(-2),
            tenant: null,
            requestBodyBytes: 4_000);

        // No usage frame: the client disconnected first.
        const string partial = "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\n";

        capture.CaptureFromSseText(partial, new UsageCaptureStats(FrameCount: 25, TotalBytes: 900));

        recorder.Received(1).Enqueue(Arg.Is<UsageEvent>(e =>
            e.RequestId == "req-cut" &&
            e.TokenSource == UsageTokenSource.Estimated &&
            e.CompletionTokens == 24 &&
            // Prompt approximated from the body the upstream actually read and charged for.
            // Leaving it at zero meant a disconnect just before the usage frame billed nothing
            // for the input, which dominates cost on long-context workloads.
            e.PromptTokens == 1_000));
        metrics.Received(1).RecordEstimatedUsage("gpt-4o");
        capture.HasEnqueuedUsage.Should().BeTrue();
    }

    /// <summary>
    /// A dropped usage event must not report as enqueued.
    /// </summary>
    /// <remarks>
    /// The router settles a request's budget reservation only when persistence will actually run.
    /// Marking the event enqueued regardless of whether the queue accepted it meant a saturated
    /// queue — which drops silently — left the reservation held for its full TTL, so sustained load
    /// accumulated phantom spend and hard-stopped tenants nowhere near their budget.
    /// </remarks>
    [Fact]
    public void CaptureFromJsonBody_WhenRecorderDropsTheEvent_DoesNotReportEnqueued()
    {
        var recorder = Substitute.For<IUsageRecorder>();
        recorder.Enqueue(Arg.Any<UsageEvent>()).Returns(false);

        var capture = new InferenceUsageCapture(
            recorder,
            Substitute.For<IGatewayMetricsCollector>(),
            "gpt-4o",
            "req-dropped",
            DateTimeOffset.UtcNow,
            tenant: null);

        capture.CaptureFromJsonBody(
            """{"usage":{"prompt_tokens":5,"completion_tokens":7}}"""u8);

        recorder.Received(1).Enqueue(Arg.Any<UsageEvent>());
        capture.HasEnqueuedUsage.Should().BeFalse();
    }

    /// <summary>
    /// A cancellation that produced no output must not have usage fabricated for it.
    /// </summary>
    [Fact]
    public void CaptureFromSseText_NoOutput_DoesNotFabricateUsage()
    {
        var recorder = Substitute.For<IUsageRecorder>();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var capture = new InferenceUsageCapture(
            recorder, metrics, "gpt-4o", "req-empty", DateTimeOffset.UtcNow, tenant: null);

        capture.CaptureFromSseText(string.Empty, new UsageCaptureStats(FrameCount: 0, TotalBytes: 0));

        recorder.DidNotReceive().Enqueue(Arg.Any<UsageEvent>());
        metrics.Received(1).RecordUsageParseFailure("gpt-4o");
        metrics.DidNotReceive().RecordEstimatedUsage(Arg.Any<string>());
        capture.HasEnqueuedUsage.Should().BeFalse();
    }

    /// <summary>Authoritative usage always wins over the estimate.</summary>
    [Fact]
    public void CaptureFromSseText_WithTerminalUsage_PrefersTheAuthoritativeCounts()
    {
        var recorder = Substitute.For<IUsageRecorder>();
        var metrics = Substitute.For<IGatewayMetricsCollector>();
        var capture = new InferenceUsageCapture(
            recorder, metrics, "gpt-4o", "req-complete", DateTimeOffset.UtcNow, tenant: null);

        const string sse = """
            data: {"choices":[{"delta":{"content":"a"}}]}

            data: {"usage":{"prompt_tokens":40,"completion_tokens":7}}

            data: [DONE]

            """;

        capture.CaptureFromSseText(sse, new UsageCaptureStats(FrameCount: 300, TotalBytes: 9000));

        recorder.Received(1).Enqueue(Arg.Is<UsageEvent>(e =>
            e.TokenSource == UsageTokenSource.Split &&
            e.PromptTokens == 40 &&
            e.CompletionTokens == 7));
        metrics.DidNotReceive().RecordEstimatedUsage(Arg.Any<string>());
    }

    /// <summary>A single streamed frame still bills at least one token rather than zero.</summary>
    [Fact]
    public void CaptureFromSseText_SingleFrame_EstimatesAtLeastOneToken()
    {
        var recorder = Substitute.For<IUsageRecorder>();
        var capture = new InferenceUsageCapture(
            recorder,
            Substitute.For<IGatewayMetricsCollector>(),
            "gpt-4o",
            "req-one",
            DateTimeOffset.UtcNow,
            tenant: null);

        capture.CaptureFromSseText("data: x\n\n", new UsageCaptureStats(FrameCount: 1, TotalBytes: 10));

        recorder.Received(1).Enqueue(Arg.Is<UsageEvent>(e => e.CompletionTokens == 1));
    }
}
