using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Forwarding;
using Pol33.Proxy.Forwarding;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class InferenceHttpForwarderTests
{
    /// <summary>Generous deadlines so timing is never what a behavioural test is really asserting.</summary>
    private static readonly InferenceForwardTimeouts TestTimeouts =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

    [Fact]
    public async Task SendAsync_Streaming_ForwardsFirstBytesBeforeUpstreamCompletes()
    {
        var handler = new DelayedSseUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(
            isStreaming: true,
            clientModelName: "gpt",
            canonicalModelId: "gpt");

        var responseBody = new SignaledResponseBodyStream();
        context.Response.Body = responseBody;

        var sendTask = forwarder.SendAsync(
            context,
            "http://backend:8000",
            upstreamBearerToken: null,
            transformer,
            isStreaming: true,
            TestTimeouts,
            CancellationToken.None);

        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var early = await Task.WhenAny(
            responseBody.FirstWriteTask,
            Task.Delay(DelayedSseUpstreamHandler.InterChunkDelay / 2, readCts.Token));

        early.Should().Be(responseBody.FirstWriteTask, "first SSE bytes should be written before upstream inter-chunk delay");

        var error = await sendTask;
        error.Should().Be(ForwarderError.None);
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        context.Response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");

        responseBody.Position = 0;
        var full = await new StreamReader(responseBody).ReadToEndAsync(readCts.Token);
        full.Should().Contain(DelayedSseUpstreamHandler.FirstChunkMarker);
        full.Should().Contain(DelayedSseUpstreamHandler.SecondChunkMarker);
    }

    [Fact]
    public async Task SendAsync_NonStreaming_UsesBufferedCompletion()
    {
        var handler = new ImmediateJsonUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: "gpt",
            canonicalModelId: "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: false,
            TestTimeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync())
            .Should().Contain(ImmediateJsonUpstreamHandler.BodyMarker);
    }

    [Fact]
    public async Task SendAsync_NonStreaming_SkipsTransferEncodingHeader()
    {
        var handler = new NonStreamingChunkedHeaderUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: "gpt",
            canonicalModelId: "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            upstreamBearerToken: null,
            transformer,
            isStreaming: false,
            TestTimeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        context.Response.Headers.ContainsKey("Transfer-Encoding").Should().BeFalse();
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("non-stream-upstream");
    }

    [Fact]
    public async Task SendAsync_UpstreamTimeout_ReturnsRequestTimedOut()
    {
        var handler = new HangingUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            TestTimeouts,
            cts.Token);

        error.Should().BeOneOf(ForwarderError.RequestTimedOut, ForwarderError.RequestCanceled);
    }

    [Fact]
    public async Task SendAsync_Streaming_ClientDisconnectDuringCopy_ReturnsRequestCanceled()
    {
        var handler = new DelayedSseUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        context.Response.Body = new ThrowingAfterFirstWriteStream();
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            TestTimeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.RequestCanceled);
    }

    [Fact]
    public async Task SendAsync_Streaming_SkipsHopByHopHeaders()
    {
        var handler = new StreamingHeadersUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            upstreamBearerToken: null,
            transformer,
            isStreaming: true,
            TestTimeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        context.Response.Headers.ContainsKey("Connection").Should().BeFalse();
        context.Response.Headers.ContainsKey("Transfer-Encoding").Should().BeFalse();
        context.Response.Headers.ContainsKey("Keep-Alive").Should().BeFalse();
        context.Response.Headers.ContainsKey("Content-Length").Should().BeFalse();
        context.Response.Headers.ContentType.ToString().Should().Contain("text/event-stream");
    }

    [Fact]
    public async Task SendAsync_Streaming_ConcurrentRequests_AllCompleteSuccessfully()
    {
        var handler = new DelayedSseUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            var context = CreatePostContext("""{"model":"gpt","stream":true}""");
            var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");
            var error = await forwarder.SendAsync(
                context,
                "http://backend:8000",
                upstreamBearerToken: null,
                transformer,
                isStreaming: true,
                TestTimeouts,
                CancellationToken.None);

            error.Should().Be(ForwarderError.None);
            context.Response.Body.Position = 0;
            var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
            body.Should().Contain(DelayedSseUpstreamHandler.FirstChunkMarker);
            body.Should().Contain(DelayedSseUpstreamHandler.SecondChunkMarker);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task SendAsync_Streaming_RecordsTimeToFirstTokenOnce()
    {
        var handler = new DelayedSseUpstreamHandler();
        var metrics = new CapturingGatewayMetricsCollector();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            metrics,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        context.Items[InferenceForwardingContextKeys.StartedUtc] = DateTimeOffset.UtcNow;
        context.Items[InferenceForwardingContextKeys.ModelId] = "gpt";
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            upstreamBearerToken: null,
            transformer,
            isStreaming: true,
            TestTimeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        metrics.TimeToFirstTokenRecords.Should().HaveCount(1);
        metrics.TimeToFirstTokenRecords[0].ModelId.Should().Be("gpt");
        metrics.TimeToFirstTokenRecords[0].Seconds.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// The defect this covers: a single total-duration deadline truncated healthy long streams. With
    /// the split deadlines, a stream that keeps producing outlives a header timeout many times over.
    /// </summary>
    [Fact]
    public async Task SendAsync_Streaming_ProducingStreamOutlivesHeaderTimeout()
    {
        var handler = new SlowDripUpstreamHandler(chunks: 6, interChunkDelay: TimeSpan.FromMilliseconds(120));
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        // Total stream duration (~720ms) far exceeds the 200ms header timeout, but each gap is well
        // inside the 2s idle timeout.
        var timeouts = new InferenceForwardTimeouts(
            HeaderTimeout: TimeSpan.FromMilliseconds(200),
            StreamIdleTimeout: TimeSpan.FromSeconds(2));

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            timeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("chunk-5");
    }

    /// <summary>
    /// A genuine mid-stream stall must be reported as ResponseBodyCanceled, which the middleware
    /// maps to "abandon the probe" rather than "backend failure".
    /// </summary>
    [Fact]
    public async Task SendAsync_Streaming_StalledUpstream_ReturnsResponseBodyCanceled()
    {
        var handler = new SlowDripUpstreamHandler(
            chunks: 2,
            interChunkDelay: TimeSpan.FromMilliseconds(20),
            stallAfterChunks: 1);

        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var timeouts = new InferenceForwardTimeouts(
            HeaderTimeout: TimeSpan.FromSeconds(5),
            StreamIdleTimeout: TimeSpan.FromMilliseconds(200));

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            timeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.ResponseBodyCanceled);
    }

    /// <summary>
    /// A non-streaming response that stalls mid-transfer is reported as ResponseBodyCanceled, the
    /// same as a stalled stream — not as a header timeout.
    /// </summary>
    /// <remarks>
    /// The defect this covers: non-streaming responses were fetched with ResponseContentRead, so the
    /// whole body was buffered inside SendAsync and its transfer was charged against the header
    /// deadline. A breach there is recorded as backend ill health and counts toward the circuit
    /// breaker, so a backend that was answering — just slowly, as a large-context request makes it —
    /// was taken out of service for every caller.
    /// </remarks>
    [Fact]
    public async Task SendAsync_NonStreaming_StalledUpstream_ReturnsResponseBodyCanceled()
    {
        var handler = new SlowDripUpstreamHandler(
            chunks: 2,
            interChunkDelay: TimeSpan.FromMilliseconds(20),
            stallAfterChunks: 1,
            contentType: "application/json");

        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(false, "gpt", "gpt");

        var timeouts = new InferenceForwardTimeouts(
            HeaderTimeout: TimeSpan.FromSeconds(5),
            StreamIdleTimeout: TimeSpan.FromMilliseconds(200));

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: false,
            timeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.ResponseBodyCanceled);
    }

    /// <summary>
    /// A non-streaming response whose transfer outlives the header deadline still completes: only the
    /// gap between chunks is bounded once the upstream has answered.
    /// </summary>
    [Fact]
    public async Task SendAsync_NonStreaming_SlowBodyOutlivesHeaderTimeout()
    {
        var handler = new SlowDripUpstreamHandler(
            chunks: 6,
            interChunkDelay: TimeSpan.FromMilliseconds(120),
            contentType: "application/json");

        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(false, "gpt", "gpt");

        var timeouts = new InferenceForwardTimeouts(
            HeaderTimeout: TimeSpan.FromMilliseconds(200),
            StreamIdleTimeout: TimeSpan.FromSeconds(2));

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: false,
            timeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("chunk-5");
    }

    /// <summary>
    /// An SSE upstream returns headers the moment it accepts the request, so for a streaming
    /// request the header allowance was spent in milliseconds and the first token had only the
    /// idle gap to arrive in — a hard time-to-first-token ceiling the header allowance had been
    /// sized precisely to avoid. The first byte must be governed by the header allowance.
    /// </summary>
    [Fact]
    public async Task SendAsync_Streaming_FirstTokenSlowerThanIdleGap_IsGovernedByTheHeaderAllowance()
    {
        // Headers at once, the only chunk after 500 ms: far past a 150 ms idle gap, well inside
        // the 5 s header allowance. A single chunk, so the idle gap — which does apply from the
        // first byte on — is never what the test exercises.
        var handler = new SlowDripUpstreamHandler(
            chunks: 1,
            interChunkDelay: TimeSpan.FromMilliseconds(500),
            stallAfterChunks: null);

        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            new InferenceForwardTimeouts(
                HeaderTimeout: TimeSpan.FromSeconds(5),
                StreamIdleTimeout: TimeSpan.FromMilliseconds(150)),
            CancellationToken.None);

        error.Should().Be(ForwarderError.None, "the first token is on the header allowance, not the idle gap");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("chunk-0");
        context.Items[InferenceForwardingContextKeys.ResponseBytesForwarded].Should().Be((long)body.Length);
    }

    /// <summary>
    /// The complement: headers and then nothing, past the header allowance, is still a stall — and
    /// the record must be able to say that nothing was forwarded, which is what separates a
    /// time-to-first-token failure from a generation that stopped mid-stream.
    /// </summary>
    [Fact]
    public async Task SendAsync_Streaming_NoFirstByteWithinTheAllowance_ReportsZeroBytesForwarded()
    {
        var handler = new SlowDripUpstreamHandler(
            chunks: 1,
            interChunkDelay: TimeSpan.Zero,
            stallAfterChunks: 0);

        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            new InferenceForwardTimeouts(
                HeaderTimeout: TimeSpan.FromMilliseconds(300),
                StreamIdleTimeout: TimeSpan.FromMilliseconds(100)),
            CancellationToken.None);

        error.Should().Be(ForwarderError.ResponseBodyCanceled);
        context.Items[InferenceForwardingContextKeys.ResponseBytesForwarded].Should().Be(0L);
        context.Items[GatewayErrorContextKeys.UpstreamException].Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Contain("no response body byte");
    }

    /// <summary>A mid-stream stall reports what did reach the client, and says so.</summary>
    [Fact]
    public async Task SendAsync_Streaming_MidStreamStall_ReportsBytesForwarded()
    {
        var handler = new SlowDripUpstreamHandler(
            chunks: 2,
            interChunkDelay: TimeSpan.FromMilliseconds(20),
            stallAfterChunks: 1);

        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            new InferenceForwardTimeouts(
                HeaderTimeout: TimeSpan.FromSeconds(5),
                StreamIdleTimeout: TimeSpan.FromMilliseconds(200)),
            CancellationToken.None);

        error.Should().Be(ForwarderError.ResponseBodyCanceled);
        ((long)context.Items[InferenceForwardingContextKeys.ResponseBytesForwarded]!).Should().BePositive();
        context.Items[GatewayErrorContextKeys.UpstreamException].Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Contain("stalled for more than").And.Contain("had reached the client");
    }

    /// <summary>
    /// The idle deadline covered the write to the client as well as the read from the upstream. A
    /// client that stopped reading blocked the flush, the timer fired, and a slow client was
    /// recorded as an upstream stall. Only upstream reads are on the read clock.
    /// </summary>
    /// <remarks>
    /// Also the deadline-replacement case: the write outlives the read gap, so the read deadline's
    /// timer fires while nothing is awaiting it. The stream must carry on afterwards, which it can
    /// only do if the cancelled source was replaced rather than re-armed — a cancelled source ignores
    /// <c>CancelAfter</c>, so the very next read would have thrown.
    /// </remarks>
    [Fact]
    public async Task SendAsync_Streaming_SlowClientWrite_IsNotReportedAsAnUpstreamStall()
    {
        var handler = new SlowDripUpstreamHandler(
            chunks: 3,
            interChunkDelay: TimeSpan.FromMilliseconds(10));

        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        // The second write — after the first byte has switched the deadline to the idle gap —
        // takes three times the gap to complete.
        context.Response.Body = new SlowClientResponseStream(
            slowWriteOrdinal: 2,
            writeDelay: TimeSpan.FromMilliseconds(450));
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            new InferenceForwardTimeouts(
                HeaderTimeout: TimeSpan.FromSeconds(5),
                StreamIdleTimeout: TimeSpan.FromMilliseconds(150))
            {
                // Comfortably longer than the slow write, and independent of the read gap: that
                // independence is the whole point of the two deadlines being separate.
                DownstreamWriteTimeout = TimeSpan.FromSeconds(5),
            },
            CancellationToken.None);

        error.Should().Be(ForwarderError.None, "a slow client is not an upstream stall");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain("chunk-0").And.Contain("chunk-1").And.Contain("chunk-2");
        context.Items[InferenceForwardingContextKeys.ResponseBytesForwarded].Should().Be((long)body.Length);
    }

    /// <summary>
    /// A client that holds the socket open and stops reading used to hold the whole forward with it:
    /// the write observed only the client's abort token, so nothing in the application bounded it.
    /// The upstream connection, the per-model bulkhead slot and the budget reservation were retained
    /// for as long as the client cared to wait, and the only thing that ended it was Kestrel's
    /// response data rate — a host default the gateway never configured and an operator may relax for
    /// long-lived SSE clients.
    /// </summary>
    [Fact]
    public async Task SendAsync_Streaming_ClientStopsReading_ReportsDownstreamWriteTimeout()
    {
        var handler = new SlowDripUpstreamHandler(chunks: 3, interChunkDelay: TimeSpan.Zero);
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var destination = new SlowClientResponseStream(slowWriteOrdinal: 1, writeDelay: TimeSpan.FromMinutes(5));
        context.Response.Body = destination;
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var sendTask = forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            new InferenceForwardTimeouts(
                // Long enough that neither upstream deadline can be the one that fires.
                HeaderTimeout: TimeSpan.FromSeconds(30),
                StreamIdleTimeout: TimeSpan.FromSeconds(30))
            {
                DownstreamWriteTimeout = TimeSpan.FromMilliseconds(150),
            },
            CancellationToken.None);

        await destination.SlowWriteEntered.WaitAsync(TimeSpan.FromSeconds(5));

        // Bounded, so a regression that removes the write bound fails the test instead of hanging the
        // suite — which is exactly how the unbounded write behaved.
        var error = await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

        error.Should().Be(ForwarderError.ResponseBodyClient);

        // Not a TimeoutException: that type makes GatewayLogHints say "the upstream did not respond",
        // which points an operator at the wrong system for a fault that is entirely downstream.
        var stashed = context.Items[GatewayErrorContextKeys.UpstreamException].Should().BeAssignableTo<Exception>().Subject;
        stashed.Should().NotBeAssignableTo<TimeoutException>();
        stashed.Message.Should().Contain("client stopped reading");

        // A write that timed out delivered nothing, so it must not inflate the count.
        context.Items[InferenceForwardingContextKeys.ResponseBytesForwarded].Should().Be(0L);
    }

    /// <summary>
    /// A genuine disconnect outranks the write deadline even when both have expired: the disconnect is
    /// the root cause, and reporting it as a gateway timeout would both mislead the operator and put a
    /// client hang-up in the error store, which <c>client_canceled</c> exists to keep out.
    /// </summary>
    [Fact]
    public async Task SendAsync_Streaming_ClientDisconnectsDuringStalledWrite_IsReportedAsCancellation()
    {
        var handler = new SlowDripUpstreamHandler(chunks: 3, interChunkDelay: TimeSpan.Zero);
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var destination = new SlowClientResponseStream(slowWriteOrdinal: 1, writeDelay: TimeSpan.FromMinutes(5));
        context.Response.Body = destination;
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        using var clientGone = new CancellationTokenSource();

        // Cancelled only once the write is genuinely in flight, so the ordering is decided by the
        // test rather than by a sleep.
        var sendTask = forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            new InferenceForwardTimeouts(
                HeaderTimeout: TimeSpan.FromSeconds(30),
                StreamIdleTimeout: TimeSpan.FromSeconds(30))
            {
                // Expires while the write is blocked, so both the write deadline and the client token
                // are cancelled by the time the write observes either.
                DownstreamWriteTimeout = TimeSpan.FromMilliseconds(50),
            },
            clientGone.Token);

        await destination.SlowWriteEntered.WaitAsync(TimeSpan.FromSeconds(5));
        await clientGone.CancelAsync();

        (await sendTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(ForwarderError.RequestCanceled);
        context.Items.ContainsKey(GatewayErrorContextKeys.UpstreamException).Should().BeFalse(
            "a client hang-up is not a gateway fault and needs no stashed cause");
    }

    /// <summary>A header timeout is distinct from both cancellation and a stream stall.</summary>
    [Fact]
    public async Task SendAsync_HeaderTimeout_ReturnsRequestTimedOut()
    {
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(new HangingUpstreamHandler()),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var timeouts = new InferenceForwardTimeouts(
            HeaderTimeout: TimeSpan.FromMilliseconds(150),
            StreamIdleTimeout: TimeSpan.FromSeconds(30));

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            timeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.RequestTimedOut);
    }

    /// <summary>
    /// An upstream that answers and then resets the connection mid-body is a backend failure, not a
    /// client hang-up. Reporting it as RequestCanceled hid a flapping backend from the breaker and
    /// the operator; for a non-streaming request nothing has reached the client, so the router can
    /// still answer with a 502 — provided the upstream's copied headers are gone again.
    /// </summary>
    [Fact]
    public async Task SendAsync_NonStreaming_UpstreamBodyReset_ReturnsResponseBodyDestinationAndClearsCopiedHeaders()
    {
        var handler = new BrokenBodyUpstreamHandler(bytesBeforeFailure: 0, contentType: "application/json");
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(false, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: false,
            TestTimeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.ResponseBodyDestination);
        context.Response.HasStarted.Should().BeFalse();
        context.Response.Headers.ContainsKey("X-Upstream-Marker").Should().BeFalse();
        context.Response.Headers.ContainsKey("Content-Type").Should().BeFalse();
    }

    /// <summary>
    /// The same failure mid-stream: some bytes are already with the client, so headers stay, but the
    /// outcome is still the backend's fault rather than the client's.
    /// </summary>
    [Fact]
    public async Task SendAsync_Streaming_UpstreamBodyReset_ReturnsResponseBodyDestination()
    {
        var handler = new BrokenBodyUpstreamHandler(bytesBeforeFailure: 1);
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            TestTimeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.ResponseBodyDestination);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.Should().Contain(BrokenBodyUpstreamHandler.ChunkMarker);
    }

    /// <summary>
    /// When the client is the one that went away, an upstream read that fails as a consequence is
    /// still the client's doing.
    /// </summary>
    [Fact]
    public async Task SendAsync_UpstreamBodyReset_AfterClientCancellation_ReturnsRequestCanceled()
    {
        using var clientGone = new CancellationTokenSource();
        var handler = new BrokenBodyUpstreamHandler(bytesBeforeFailure: 0, onFirstRead: clientGone.Cancel);
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: true,
            TestTimeouts,
            clientGone.Token);

        error.Should().Be(ForwarderError.RequestCanceled);
    }

    /// <summary>
    /// Upstream headers that would let the backend speak for the gateway — CORS decisions, cookies,
    /// auth challenges, server banners — are never relayed. Ordinary provider headers still are.
    /// </summary>
    [Fact]
    public async Task SendAsync_DoesNotRelayGatewayOwnedResponseHeaders()
    {
        var handler = new LeakyHeadersUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        context.Response.Headers["Access-Control-Allow-Origin"] = "https://gateway.example";
        var transformer = new StreamingHttpTransformer(false, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            "http://backend:8000",
            null,
            transformer,
            isStreaming: false,
            TestTimeouts,
            CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        context.Response.Headers["Access-Control-Allow-Origin"].ToString().Should().Be("https://gateway.example");
        context.Response.Headers.ContainsKey("Access-Control-Allow-Credentials").Should().BeFalse();
        context.Response.Headers.ContainsKey("Set-Cookie").Should().BeFalse();
        context.Response.Headers.ContainsKey("WWW-Authenticate").Should().BeFalse();
        context.Response.Headers.ContainsKey("Server").Should().BeFalse();
        context.Response.Headers.ContainsKey("Via").Should().BeFalse();
        context.Response.Headers.ContainsKey("X-Powered-By").Should().BeFalse();
        context.Response.Headers["x-request-id"].ToString().Should().Be("req-upstream");
        context.Response.Headers["x-ratelimit-remaining-requests"].ToString().Should().Be("41");
    }

    /// <summary>
    /// The upstream's error body is the only thing that says why a model rejected a call. It is
    /// stashed for the error store while still reaching the client byte-for-byte.
    /// </summary>
    [Fact]
    public async Task SendAsync_Upstream400_StashesBodySnippetAndForwardsBody()
    {
        var handler = new StatusUpstreamHandler(HttpStatusCode.BadRequest, StatusUpstreamHandler.ErrorBody);
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance,
            Options.Create(new GatewayErrorTrackingOptions()));

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: "gpt",
            canonicalModelId: "gpt");

        var error = await forwarder.SendAsync(
            context, "http://backend:8000", null, transformer, isStreaming: false, TestTimeouts, CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        context.Response.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
        context.Items[GatewayErrorContextKeys.UpstreamBodySnippet].Should().Be(StatusUpstreamHandler.ErrorBody);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).Should().Be(StatusUpstreamHandler.ErrorBody);
    }

    [Fact]
    public async Task SendAsync_Upstream400_TruncatesSnippetToConfiguredBytes()
    {
        var handler = new StatusUpstreamHandler(HttpStatusCode.BadRequest, new string('x', 5000));
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance,
            Options.Create(new GatewayErrorTrackingOptions { UpstreamBodySnippetBytes = 64 }));

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(isStreaming: false, clientModelName: "gpt", canonicalModelId: "gpt");

        await forwarder.SendAsync(
            context, "http://backend:8000", null, transformer, isStreaming: false, TestTimeouts, CancellationToken.None);

        ((string)context.Items[GatewayErrorContextKeys.UpstreamBodySnippet]!).Length.Should().Be(64);
        context.Response.Body.Length.Should().Be(5000, "the client must still receive the whole body");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendAsync_SuccessOrCaptureDisabled_DoesNotStashSnippet(bool captureEnabled)
    {
        var handler = new StatusUpstreamHandler(
            captureEnabled ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
            StatusUpstreamHandler.ErrorBody);
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance,
            Options.Create(new GatewayErrorTrackingOptions { CaptureUpstreamBodySnippet = captureEnabled }));

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(isStreaming: false, clientModelName: "gpt", canonicalModelId: "gpt");

        await forwarder.SendAsync(
            context, "http://backend:8000", null, transformer, isStreaming: false, TestTimeouts, CancellationToken.None);

        context.Items.ContainsKey(GatewayErrorContextKeys.UpstreamBodySnippet).Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_CompressedUpstreamError_DoesNotStashBinarySnippet()
    {
        var handler = new StatusUpstreamHandler(HttpStatusCode.BadRequest, StatusUpstreamHandler.ErrorBody, contentEncoding: "gzip");
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance,
            Options.Create(new GatewayErrorTrackingOptions()));

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(isStreaming: false, clientModelName: "gpt", canonicalModelId: "gpt");

        await forwarder.SendAsync(
            context, "http://backend:8000", null, transformer, isStreaming: false, TestTimeouts, CancellationToken.None);

        context.Items.ContainsKey(GatewayErrorContextKeys.UpstreamBodySnippet).Should().BeFalse();
    }

    private static DefaultHttpContext CreatePostContext(string jsonBody)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        var context = new DefaultHttpContext
        {
            Request =
            {
                Method = HttpMethods.Post,
                Path = "/v1/chat/completions",
                Body = new MemoryStream(bodyBytes),
                ContentType = "application/json",
                ContentLength = bodyBytes.Length,
            },
            Response = { Body = new MemoryStream() },
        };
        context.Request.EnableBuffering();
        return context;
    }

    /// <summary>
    /// A client-side response body whose Nth write takes a configurable time to complete. A delay
    /// longer than the test is how a client that has stopped reading behaves: the write sits there
    /// until something cancels its token.
    /// </summary>
    /// <remarks>
    /// Only <c>WriteAsync</c> blocks. <c>Response.StartAsync</c> flushes before the body phase is
    /// entered, so a double that also blocks <c>FlushAsync</c> hangs before the write deadline is
    /// ever armed — which is a property of the harness, not of the code under test.
    /// </remarks>
    private sealed class SlowClientResponseStream(int slowWriteOrdinal, TimeSpan writeDelay) : MemoryStream
    {
        private readonly TaskCompletionSource _slowWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        /// <summary>Completes once the slow write is in flight, so a test orders against it without sleeping.</summary>
        public Task SlowWriteEntered => _slowWriteEntered.Task;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writes) == slowWriteOrdinal)
            {
                _slowWriteEntered.TrySetResult();
                await Task.Delay(writeDelay, cancellationToken);
            }

            await base.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class SignaledResponseBodyStream : MemoryStream
    {
        private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        public Task FirstWriteTask => _firstWrite.Task;

        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            SignalFirstWrite();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var write = base.WriteAsync(buffer, offset, count, cancellationToken);
            SignalFirstWrite();
            return write;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var write = base.WriteAsync(buffer, cancellationToken);
            SignalFirstWrite();
            return write;
        }

        private void SignalFirstWrite()
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                _firstWrite.TrySetResult();
            }
        }
    }

    [Fact]
    public async Task SendAsync_WhenTheUpstreamRefusesTheConnection_StashesTheExceptionForTheErrorRecord()
    {
        var handler = new ThrowingUpstreamHandler(new HttpRequestException(
            "Connection refused",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)));
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
            NoOpGatewayMetricsCollector.Instance,
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":false}""");
        var transformer = new StreamingHttpTransformer(false, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context, "http://backend:8000", upstreamBearerToken: null, transformer, isStreaming: false, TestTimeouts, CancellationToken.None);

        error.Should().Be(ForwarderError.Request);
        context.Items[GatewayErrorContextKeys.UpstreamException].Should().BeOfType<HttpRequestException>()
            .Which.InnerException.Should().BeOfType<System.Net.Sockets.SocketException>();
    }

    private sealed class ThrowingUpstreamHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class SingleHandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NoOpGatewayMetricsCollector : IGatewayMetricsCollector
    {
        public static readonly NoOpGatewayMetricsCollector Instance = new();

        public void RecordRateLimitRejection(string reason) { }
        public void RecordQuotaRejection() { }
        public void RecordTokenUsage(string modelId, long promptTokens, long completionTokens) { }
        public void RecordEstimatedUsage(string modelId)
        {
        }

        public void RecordUnsplitUsage(string modelId)
        {
        }

        public void RecordUsageParseFailure(string modelId) { }
        public void RecordInferenceRouted(string modelId, string route, bool isStreaming) { }
        public void RecordForwardAttempt(string modelId, string outcome) { }
        public void RecordModelResolve(string result) { }
        public void RecordCircuitBreakerTransition(string modelId, string toState) { }
        public void RecordBulkheadRejection(string modelId) { }
        public void RecordBulkheadInflightChange(string modelId, int delta) { }
        public void RecordTimeToFirstToken(string modelId, double seconds) { }

        public void RecordBillingReconciliation(int discrepancyCount, double absoluteCostDrift) { }
    }

    private sealed class CapturingGatewayMetricsCollector : IGatewayMetricsCollector
    {
        public List<(string ModelId, double Seconds)> TimeToFirstTokenRecords { get; } = [];

        public void RecordRateLimitRejection(string reason) { }
        public void RecordQuotaRejection() { }
        public void RecordTokenUsage(string modelId, long promptTokens, long completionTokens) { }
        public void RecordEstimatedUsage(string modelId)
        {
        }

        public void RecordUnsplitUsage(string modelId)
        {
        }

        public void RecordUsageParseFailure(string modelId) { }
        public void RecordInferenceRouted(string modelId, string route, bool isStreaming) { }
        public void RecordForwardAttempt(string modelId, string outcome) { }
        public void RecordModelResolve(string result) { }
        public void RecordCircuitBreakerTransition(string modelId, string toState) { }
        public void RecordBulkheadRejection(string modelId) { }
        public void RecordBulkheadInflightChange(string modelId, int delta) { }

        public void RecordTimeToFirstToken(string modelId, double seconds) =>
            TimeToFirstTokenRecords.Add((modelId, seconds));

        public void RecordBillingReconciliation(int discrepancyCount, double absoluteCostDrift) { }
    }

    private sealed class DelayedSseUpstreamHandler : HttpMessageHandler
    {
        public const string FirstChunkMarker = "sse-first";
        public const string SecondChunkMarker = "sse-second";
        public static readonly TimeSpan InterChunkDelay = TimeSpan.FromMilliseconds(500);

        protected override HttpResponseMessage Send(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken).GetAwaiter().GetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var stream = new DelayedSseStream();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") },
                },
            };
        }

        private sealed class DelayedSseStream : Stream
        {
            private int _phase;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (_phase == 0)
                {
                    var first = Encoding.UTF8.GetBytes($"data: {{\"m\":\"{FirstChunkMarker}\"}}\n\n");
                    first.AsSpan(0, Math.Min(first.Length, buffer.Length)).CopyTo(buffer.Span);
                    _phase = 1;
                    return Math.Min(first.Length, buffer.Length);
                }

                if (_phase == 1)
                {
                    await Task.Delay(InterChunkDelay, cancellationToken).ConfigureAwait(false);
                    var second = Encoding.UTF8.GetBytes($"data: {{\"m\":\"{SecondChunkMarker}\"}}\n\n");
                    second.AsSpan(0, Math.Min(second.Length, buffer.Length)).CopyTo(buffer.Span);
                    _phase = 2;
                    return Math.Min(second.Length, buffer.Length);
                }

                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class StreamingHeadersUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"m\":\"ok\"}\n\n", Encoding.UTF8, "text/event-stream"),
            };
            response.Headers.Connection.Add("keep-alive");
            response.Headers.TransferEncodingChunked = true;
            response.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingAfterFirstWriteStream : MemoryStream
    {
        private bool _written;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_written)
            {
                throw new IOException("Simulated client disconnect.");
            }

            _written = true;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class StatusUpstreamHandler(HttpStatusCode status, string body, string? contentEncoding = null)
        : HttpMessageHandler
    {
        public const string ErrorBody = """{"error":{"message":"'system' role is not supported by this model","type":"invalid_request_error"}}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (contentEncoding is not null)
            {
                response.Content.Headers.ContentEncoding.Add(contentEncoding);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class ImmediateJsonUpstreamHandler : HttpMessageHandler
    {
        public const string BodyMarker = "json-upstream";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"id":"1","object":"chat.completion","choices":[],"marker":"{{BodyMarker}}"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
    }

    private sealed class NonStreamingChunkedHeaderUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"marker":"non-stream-upstream"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            response.Headers.TransferEncodingChunked = true;
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Emits <c>chunks</c> SSE frames separated by <c>interChunkDelay</c>. When
    /// <c>stallAfterChunks</c> is set, the stream stops producing (without completing) after that
    /// many frames, which is what a hung upstream looks like to the gateway.
    /// </summary>
    private sealed class SlowDripUpstreamHandler(
        int chunks,
        TimeSpan interChunkDelay,
        int? stallAfterChunks = null,
        string contentType = "text/event-stream") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowDripStream(chunks, interChunkDelay, stallAfterChunks))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue(contentType) },
                },
            });

        private sealed class SlowDripStream(
            int chunks,
            TimeSpan interChunkDelay,
            int? stallAfterChunks) : Stream
        {
            private int _emitted;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (stallAfterChunks is int stallAt && _emitted >= stallAt)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                }

                if (_emitted >= chunks)
                {
                    return 0;
                }

                await Task.Delay(interChunkDelay, cancellationToken).ConfigureAwait(false);
                var payload = Encoding.UTF8.GetBytes($"data: {{\"chunk\":\"chunk-{_emitted}\"}}\n\n");
                _emitted++;
                payload.CopyTo(buffer);
                return payload.Length;
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Answers 200 with headers, emits <c>bytesBeforeFailure</c> SSE frames, then fails the body read
    /// the way a reset connection surfaces from HttpClient (<see cref="HttpIOException"/>).
    /// </summary>
    private sealed class BrokenBodyUpstreamHandler(
        int bytesBeforeFailure,
        string contentType = "text/event-stream",
        Action? onFirstRead = null) : HttpMessageHandler
    {
        public const string ChunkMarker = "before-reset";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BrokenStream(bytesBeforeFailure, onFirstRead))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue(contentType) },
                },
            };
            response.Headers.TryAddWithoutValidation("X-Upstream-Marker", "present");
            return Task.FromResult(response);
        }

        private sealed class BrokenStream(int chunksBeforeFailure, Action? onFirstRead) : Stream
        {
            private int _reads;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (_reads == 0)
                {
                    onFirstRead?.Invoke();
                }

                if (_reads++ < chunksBeforeFailure)
                {
                    var payload = Encoding.UTF8.GetBytes($"data: {{\"m\":\"{ChunkMarker}\"}}\n\n");
                    payload.CopyTo(buffer);
                    return ValueTask.FromResult(payload.Length);
                }

                throw new HttpIOException(
                    HttpRequestError.ResponseEnded,
                    "The response ended prematurely.");
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class LeakyHeadersUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*");
            response.Headers.TryAddWithoutValidation("Access-Control-Allow-Credentials", "true");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "session=abc; Path=/");
            response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer realm=\"upstream\"");
            response.Headers.TryAddWithoutValidation("Server", "nginx/1.25");
            response.Headers.TryAddWithoutValidation("Via", "1.1 cdn");
            response.Headers.TryAddWithoutValidation("X-Powered-By", "Express");
            response.Headers.TryAddWithoutValidation("x-request-id", "req-upstream");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", "41");
            return Task.FromResult(response);
        }
    }

    private sealed class HangingUpstreamHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("Handler should not complete.");
        }
    }
}
