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

        recorder.Received(1).Enqueue(Arg.Is<UsageEvent>(e =>
            e.RequestId == "req-rerank" &&
            e.ModelId == "reranker" &&
            e.PromptTokens == 56 &&
            e.CompletionTokens == 0));
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
}
