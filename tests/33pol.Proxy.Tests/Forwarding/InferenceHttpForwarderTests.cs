using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Core.Forwarding;
using Pol33.Proxy.Forwarding;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class InferenceHttpForwarderTests
{
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
            CancellationToken.None);

        error.Should().Be(ForwarderError.None);
        metrics.TimeToFirstTokenRecords.Should().HaveCount(1);
        metrics.TimeToFirstTokenRecords[0].ModelId.Should().Be("gpt");
        metrics.TimeToFirstTokenRecords[0].Seconds.Should().BeGreaterThanOrEqualTo(0);
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
        public void RecordUsageParseFailure(string modelId) { }
        public void RecordInferenceRouted(string modelId, string route, bool isStreaming) { }
        public void RecordForwardAttempt(string modelId, string outcome) { }
        public void RecordModelResolve(string result) { }
        public void RecordCircuitBreakerTransition(string modelId, string toState) { }
        public void RecordBulkheadRejection(string modelId) { }
        public void RecordBulkheadInflightChange(string modelId, int delta) { }
        public void RecordTimeToFirstToken(string modelId, double seconds) { }
    }

    private sealed class CapturingGatewayMetricsCollector : IGatewayMetricsCollector
    {
        public List<(string ModelId, double Seconds)> TimeToFirstTokenRecords { get; } = [];

        public void RecordRateLimitRejection(string reason) { }
        public void RecordQuotaRejection() { }
        public void RecordTokenUsage(string modelId, long promptTokens, long completionTokens) { }
        public void RecordUsageParseFailure(string modelId) { }
        public void RecordInferenceRouted(string modelId, string route, bool isStreaming) { }
        public void RecordForwardAttempt(string modelId, string outcome) { }
        public void RecordModelResolve(string result) { }
        public void RecordCircuitBreakerTransition(string modelId, string toState) { }
        public void RecordBulkheadRejection(string modelId) { }
        public void RecordBulkheadInflightChange(string modelId, int delta) { }

        public void RecordTimeToFirstToken(string modelId, double seconds) =>
            TimeToFirstTokenRecords.Add((modelId, seconds));
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
