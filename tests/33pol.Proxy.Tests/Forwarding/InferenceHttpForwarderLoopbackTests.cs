using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pol33.Core.Abstractions;
using Pol33.Proxy.Forwarding;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Tests.Forwarding;

/// <summary>
/// Forwarder tests that run against a real TCP upstream through a real
/// <see cref="SocketsHttpHandler"/>, rather than a stubbed <see cref="HttpMessageHandler"/>.
/// </summary>
/// <remarks>
/// <para>The rest of the forwarder suite drives a stub <see cref="HttpMessageHandler"/>, which can
/// only ever exercise the gateway's own logic: the response body it hands back is an ordinary
/// <see cref="Stream"/> with no connection behind it, so nothing it does can depend on how
/// <see cref="SocketsHttpHandler"/> actually treats deadlines, cancellation or connection teardown.
/// A test written there cannot distinguish "the forwarder keeps the stream alive" from "there was
/// never a connection to drop".</para>
///
/// <para>These tests exist to pin the split-deadline contract against the real handler: the header
/// allowance governs the wait for response headers and stops there, and once the upstream has
/// answered only the idle deadline can end the response. Re-introducing any whole-exchange deadline
/// — an <c>HttpClient.Timeout</c>, or a <see cref="CancellationTokenSource"/> that outlives the
/// header phase in a future runtime that does propagate it — would truncate healthy long
/// generations, and only a real socket can catch that.</para>
/// </remarks>
public sealed class InferenceHttpForwarderLoopbackTests
{
    /// <summary>
    /// The header allowance must cover the wait for response headers and nothing else. Once the
    /// upstream has answered, a body that keeps producing is governed by the idle deadline alone, so
    /// a generation many times longer than the header allowance completes intact.
    /// </summary>
    /// <remarks>
    /// Verified here against a real connection rather than a stub, because the guarantee is a joint
    /// property of the forwarder and the HTTP stack. The stub-based
    /// <c>SendAsync_Streaming_ProducingStreamOutlivesHeaderTimeout</c> asserts the same outcome but
    /// cannot observe the connection at all.
    /// </remarks>
    [Fact]
    public async Task SendAsync_Streaming_RealUpstream_ProducingStreamOutlivesHeaderTimeout()
    {
        const int chunkCount = 8;
        await using var upstream = LoopbackSseUpstream.Start(
            chunkCount,
            interChunkDelay: TimeSpan.FromMilliseconds(150));

        var forwarder = new InferenceHttpForwarder(
            new SocketsClientFactory(),
            Substitute.For<IGatewayMetricsCollector>(),
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(
            isStreaming: true,
            clientModelName: "gpt",
            canonicalModelId: "gpt");

        // ~1.2s of streaming against a 400ms header allowance. Every inter-chunk gap is far inside
        // the 10s idle deadline, so nothing here is a genuine stall.
        var timeouts = new InferenceForwardTimeouts(
            HeaderTimeout: TimeSpan.FromMilliseconds(400),
            StreamIdleTimeout: TimeSpan.FromSeconds(10));

        var error = await forwarder.SendAsync(
            context,
            upstream.BaseUrl,
            upstreamBearerToken: null,
            transformer,
            isStreaming: true,
            timeouts,
            CancellationToken.None);

        error.Should().Be(
            ForwarderError.None,
            "a body that keeps producing is governed by the idle deadline, not the header deadline");

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        for (var i = 0; i < chunkCount; i++)
        {
            body.Should().Contain($"chunk-{i}", "the client must receive the whole stream");
        }
    }

    /// <summary>
    /// The complement: when the upstream never answers at all, the header allowance must still fire
    /// and be reported as a backend health signal.
    /// </summary>
    [Fact]
    public async Task SendAsync_RealUpstream_SilentBeforeHeaders_ReturnsRequestTimedOut()
    {
        await using var upstream = LoopbackSseUpstream.Start(
            chunkCount: 1,
            interChunkDelay: TimeSpan.Zero,
            delayBeforeHeaders: TimeSpan.FromSeconds(30));

        var forwarder = new InferenceHttpForwarder(
            new SocketsClientFactory(),
            Substitute.For<IGatewayMetricsCollector>(),
            NullLogger<InferenceHttpForwarder>.Instance);

        var context = CreatePostContext("""{"model":"gpt","stream":true}""");
        var transformer = new StreamingHttpTransformer(true, "gpt", "gpt");

        var error = await forwarder.SendAsync(
            context,
            upstream.BaseUrl,
            upstreamBearerToken: null,
            transformer,
            isStreaming: true,
            new InferenceForwardTimeouts(
                HeaderTimeout: TimeSpan.FromMilliseconds(300),
                StreamIdleTimeout: TimeSpan.FromSeconds(10)),
            CancellationToken.None);

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

    /// <summary>
    /// A real <see cref="SocketsHttpHandler"/> with the production client's infinite timeout, so the
    /// only deadlines in play are the forwarder's own.
    /// </summary>
    private sealed class SocketsClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    /// <summary>
    /// Minimal chunked-SSE upstream on a loopback socket. Speaks just enough HTTP/1.1 to get a real
    /// connection established; the point is the socket, not the protocol coverage.
    /// </summary>
    private sealed class LoopbackSseUpstream : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _acceptLoop;

        private LoopbackSseUpstream(
            TcpListener listener,
            int chunkCount,
            TimeSpan interChunkDelay,
            TimeSpan delayBeforeHeaders)
        {
            _listener = listener;
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            _acceptLoop = AcceptAsync(chunkCount, interChunkDelay, delayBeforeHeaders, _stopping.Token);
        }

        public string BaseUrl { get; }

        public static LoopbackSseUpstream Start(
            int chunkCount,
            TimeSpan interChunkDelay,
            TimeSpan? delayBeforeHeaders = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new LoopbackSseUpstream(
                listener,
                chunkCount,
                interChunkDelay,
                delayBeforeHeaders ?? TimeSpan.Zero);
        }

        private async Task AcceptAsync(
            int chunkCount,
            TimeSpan interChunkDelay,
            TimeSpan delayBeforeHeaders,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    await using var stream = client.GetStream();
                    await ServeAsync(stream, chunkCount, interChunkDelay, delayBeforeHeaders, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The client hung up (which is precisely what the regression under test does when it
                // fires). Nothing to report from the fake upstream's side.
            }
        }

        private static async Task ServeAsync(
            NetworkStream stream,
            int chunkCount,
            TimeSpan interChunkDelay,
            TimeSpan delayBeforeHeaders,
            CancellationToken cancellationToken)
        {
            await DrainRequestAsync(stream, cancellationToken).ConfigureAwait(false);

            if (delayBeforeHeaders > TimeSpan.Zero)
            {
                await Task.Delay(delayBeforeHeaders, cancellationToken).ConfigureAwait(false);
            }

            await WriteAsync(
                stream,
                "HTTP/1.1 200 OK\r\n"
                + "Content-Type: text/event-stream\r\n"
                + "Transfer-Encoding: chunked\r\n"
                + "\r\n",
                cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < chunkCount; i++)
            {
                if (interChunkDelay > TimeSpan.Zero)
                {
                    await Task.Delay(interChunkDelay, cancellationToken).ConfigureAwait(false);
                }

                var payload = $"data: {{\"chunk\":\"chunk-{i}\"}}\n\n";
                await WriteAsync(
                    stream,
                    $"{payload.Length:X}\r\n{payload}\r\n",
                    cancellationToken).ConfigureAwait(false);
            }

            await WriteAsync(stream, "0\r\n\r\n", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Reads the request head and any declared body. The body must be consumed or the client's
        /// send never completes and the test hangs rather than failing.
        /// </summary>
        private static async Task DrainRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var head = new StringBuilder();
            var single = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                head.Append((char)single[0]);
            }

            var contentLength = 0;
            foreach (var line in head.ToString().Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                }
            }

            var remaining = contentLength;
            var buffer = new byte[4096];
            while (remaining > 0)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                remaining -= read;
            }
        }

        private static async Task WriteAsync(NetworkStream stream, string text, CancellationToken cancellationToken)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }

            _stopping.Dispose();
        }
    }
}
