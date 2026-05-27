using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
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
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(
            isStreaming: true,
            clientModelName: "gpt",
            canonicalModelId: "gpt");

        var sendTask = forwarder.SendAsync(
            context,
            "http://backend:8000",
            upstreamBearerToken: null,
            transformer,
            isStreaming: true,
            CancellationToken.None);

        var responseBody = context.Response.Body;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var firstRead = responseBody.ReadAsync(new byte[64], readCts.Token).AsTask();
        var early = await Task.WhenAny(
            firstRead,
            Task.Delay(DelayedSseUpstreamHandler.InterChunkDelay / 2, readCts.Token));

        early.Should().Be(firstRead, "first SSE bytes should arrive before upstream inter-chunk delay");
        (await firstRead).Should().BeGreaterThan(0);

        var error = await sendTask;
        error.Should().Be(ForwarderError.None);

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
    public async Task SendAsync_UpstreamTimeout_ReturnsRequestTimedOut()
    {
        var handler = new HangingUpstreamHandler();
        var forwarder = new InferenceHttpForwarder(
            new SingleHandlerClientFactory(handler),
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

        error.Should().Be(ForwarderError.RequestTimedOut);
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

    private sealed class SingleHandlerClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
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
