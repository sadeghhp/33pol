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
