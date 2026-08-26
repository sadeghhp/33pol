using System.Buffers;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Errors;
using Pol33.Core.Forwarding;
using Pol33.Proxy.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Forwarding;

public interface IInferenceHttpForwarder
{
    /// <summary>
    /// Forwards the current request to <paramref name="modelUrl"/>.
    /// </summary>
    /// <param name="timeouts">
    /// Header and stream-idle deadlines. The forwarder owns both, so the caller must pass an
    /// undeadlined <paramref name="cancellationToken"/> (normally <c>HttpContext.RequestAborted</c>)
    /// and rely on the returned <see cref="ForwarderError"/> to tell the two apart.
    /// </param>
    /// <param name="cancellationToken">
    /// Client-disconnect / shutdown token only. It must NOT carry a total-duration deadline —
    /// doing so is what previously truncated healthy long streams.
    /// </param>
    /// <returns>
    /// <see cref="ForwarderError.None"/> on success;
    /// <see cref="ForwarderError.Request"/> when the upstream could not be reached or rejected the
    /// request before answering (a backend health signal);
    /// <see cref="ForwarderError.RequestTimedOut"/> when headers did not arrive in time (a backend
    /// health signal);
    /// <see cref="ForwarderError.ResponseBodyCanceled"/> when a body stalled past the idle timeout
    /// (inconclusive — abandon the probe);
    /// <see cref="ForwarderError.ResponseBodyDestination"/> when the upstream answered but its body
    /// then failed mid-transfer — connection reset, premature EOF, malformed framing (a backend
    /// health signal; the client may already have received part of the body);
    /// <see cref="ForwarderError.RequestCanceled"/> when the client went away.
    /// </returns>
    /// <remarks>
    /// When an error is reported before the response has started, any upstream headers already
    /// copied onto the response have been removed again, so the caller can write a gateway error
    /// over a clean header set.
    /// </remarks>
    Task<ForwarderError> SendAsync(
        HttpContext context,
        string modelUrl,
        string? upstreamBearerToken,
        StreamingHttpTransformer transformer,
        bool isStreaming,
        InferenceForwardTimeouts timeouts,
        CancellationToken cancellationToken);
}

public sealed class InferenceHttpForwarder(
    IHttpClientFactory httpClientFactory,
    IGatewayMetricsCollector metricsCollector,
    ILogger<InferenceHttpForwarder> logger,
    IOptions<GatewayErrorTrackingOptions>? errorTracking = null) : IInferenceHttpForwarder
{
    public const string HttpClientName = Core.Http.UpstreamHttpClientNames.Inference;

    public async Task<ForwarderError> SendAsync(
        HttpContext context,
        string modelUrl,
        string? upstreamBearerToken,
        StreamingHttpTransformer transformer,
        bool isStreaming,
        InferenceForwardTimeouts timeouts,
        CancellationToken cancellationToken)
    {
        var destinationPrefix = InferenceDestinationBuilder.ToForwarderDestination(modelUrl);

        // The outbound URI is derived once, by the transformer's request step below.
        using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), (Uri?)null);

        if (HasRequestBody(context.Request))
        {
            context.Request.Body.Position = 0;
            requestMessage.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType) &&
                MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType))
            {
                requestMessage.Content.Headers.ContentType = contentType;
            }
        }

        if (!string.IsNullOrWhiteSpace(upstreamBearerToken))
        {
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstreamBearerToken);
        }

        await transformer
            .TransformRequestAsync(context, requestMessage, destinationPrefix, cancellationToken)
            .ConfigureAwait(false);

        var client = httpClientFactory.CreateClient(HttpClientName);

        // ResponseHeadersRead in both modes. Buffering a non-streaming response held the whole body
        // in memory before a single byte could be forwarded, and — because the buffering happened
        // inside SendAsync — it charged the transfer of a large body against the header deadline,
        // where a breach is recorded as backend ill health.
        const HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead;

        // Header phase. Only this stretch carries the header deadline; for both modes SendAsync
        // returns as soon as headers arrive, so it never reaches the body. The client is configured
        // with an infinite HttpClient.Timeout, so the two linked-token filters below are the only
        // cancellation sources here.
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCts.CancelAfter(timeouts.HeaderTimeout);

        HttpResponseMessage responseMessage;
        try
        {
            responseMessage = await client
                .SendAsync(requestMessage, completionOption, headerCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ForwarderError.RequestCanceled;
        }
        catch (OperationCanceledException) when (headerCts.IsCancellationRequested)
        {
            logger.LogWarning(
                "Upstream did not return response headers within {HeaderTimeoutSeconds}s for {Method} {Uri}",
                timeouts.HeaderTimeout.TotalSeconds,
                requestMessage.Method,
                requestMessage.RequestUri);
            StashException(context, new TimeoutException(
                $"Upstream did not return response headers within {timeouts.HeaderTimeout.TotalSeconds}s."));
            return ForwarderError.RequestTimedOut;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream HTTP request failed for {Method} {Uri}", requestMessage.Method, requestMessage.RequestUri);
            StashException(context, ex);
            return ForwarderError.Request;
        }

        // Headers are in, so the header allowance has done its job: disarm it rather than leaving a
        // timer to fire minutes later against a token nothing observes any more.
        //
        // Not a correctness fix on the current runtime — with ResponseHeadersRead, SocketsHttpHandler
        // does not tear the response body down when the send token is cancelled after the headers
        // have been read (verified against a real socket in InferenceHttpForwarderLoopbackTests).
        // It is still wrong to leave armed: it costs a pointless timer per in-flight request, and it
        // is the kind of latent whole-exchange deadline that client.Timeout was deliberately set to
        // Timeout.InfiniteTimeSpan to avoid.
        headerCts.CancelAfter(Timeout.InfiniteTimeSpan);

        using (responseMessage)
        {
            // Names of the upstream headers copied onto the response, so they can be removed again
            // if the body phase fails before anything reached the client. Without this a gateway 502
            // written over them carried the upstream's Content-Type/Content-Length and vendor headers.
            List<string>? copiedHeaderNames = null;
            try
            {
                context.Response.StatusCode = (int)responseMessage.StatusCode;

                if (isStreaming)
                {
                    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                }

                var transformBody = await transformer
                    .TransformResponseAsync(context, responseMessage, cancellationToken)
                    .ConfigureAwait(false);

                if (!transformBody || responseMessage.Content is null)
                {
                    return ForwarderError.None;
                }

                copiedHeaderNames = CopyResponseHeaders(context, responseMessage, isStreaming);

                if (isStreaming)
                {
                    ApplyStreamingResponseHeaders(context);
                    await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);
                }

                // Body phase, for streaming and non-streaming alike. The idle deadline is rearmed
                // after every chunk that reaches the client, so a response of any total duration
                // survives while the upstream keeps producing. Only a genuine stall trips it, and a
                // stall is inconclusive about backend health because the backend already answered.
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(timeouts.StreamIdleTimeout);

                await using var upstreamBody = await responseMessage.Content
                    .ReadAsStreamAsync(idleCts.Token)
                    .ConfigureAwait(false);

                // An upstream error body is the only thing that says *why* a model rejected a call
                // (unsupported parameter, context length, wrong role...). Without it every 400 in the
                // Errors tab read as "check your config". Only error responses are tee'd, capped at
                // the configured snippet size, so the success and streaming paths are untouched.
                var snippet = CreateSnippetBufferIfErrorResponse(responseMessage);

                try
                {
                    await CopyStreamWithFlushAsync(
                            upstreamBody,
                            context.Response.Body,
                            snippet,
                            // Time to first token is a streaming notion; a buffered response has no
                            // meaningful first-token moment to report.
                            onFirstByteWritten: isStreaming
                                ? () => RecordTimeToFirstTokenIfNeeded(context)
                                : null,
                            onChunkForwarded: () => idleCts.CancelAfter(timeouts.StreamIdleTimeout),
                            flushEachChunk: isStreaming,
                            idleCts.Token)
                        .ConfigureAwait(false);
                    StashSnippet(context, snippet);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested &&
                                                        !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Upstream stalled for more than {StreamIdleTimeoutSeconds}s while sending the response body for {Uri}",
                        timeouts.StreamIdleTimeout.TotalSeconds,
                        requestMessage.RequestUri);
                    RemoveCopiedHeadersIfNotStarted(context, copiedHeaderNames);
                    StashException(context, new TimeoutException(
                        $"Upstream stalled for more than {timeouts.StreamIdleTimeout.TotalSeconds}s while sending the response body."));
                    return ForwarderError.ResponseBodyCanceled;
                }
                catch (UpstreamBodyReadException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // The upstream answered and then broke the body off: connection reset, premature
                    // EOF, bad chunk framing. Unlike a client hang-up this is the backend's doing, so
                    // it is reported as a backend failure — otherwise a flapping backend that drops
                    // connections mid-response was invisible to the breaker and to the operator, and
                    // a non-streaming client got the upstream's 200 with a truncated body.
                    logger.LogWarning(
                        ex.InnerException,
                        "Upstream response body failed mid-transfer for {Method} {Uri}",
                        requestMessage.Method,
                        requestMessage.RequestUri);
                    RemoveCopiedHeadersIfNotStarted(context, copiedHeaderNames);
                    StashException(context, ex.InnerException ?? ex);
                    return ForwarderError.ResponseBodyDestination;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ForwarderError.RequestCanceled;
            }
            catch (UpstreamBodyReadException) when (cancellationToken.IsCancellationRequested)
            {
                // The upstream read failed as a consequence of the client going away (the aborted
                // request tears the upstream connection down too), so the client is the cause.
                return ForwarderError.RequestCanceled;
            }
            catch (IOException)
            {
                // Writing to the client failed: the client disconnected while receiving the body.
                // Reads from the upstream never surface here — they are wrapped above.
                return ForwarderError.RequestCanceled;
            }
        }

        return ForwarderError.None;
    }

    /// <summary>
    /// Copies the upstream's response headers onto the client response and returns the names that
    /// were copied.
    /// </summary>
    /// <remarks>
    /// A denylist rather than a copy-everything: besides the hop-by-hop set, anything that would let
    /// the upstream speak for the gateway is dropped. <c>Access-Control-*</c> would overwrite the
    /// origin the gateway's own CORS policy chose; <c>Set-Cookie</c> and <c>WWW-Authenticate</c>
    /// belong to the upstream's session with the gateway, not to the client's session with the
    /// gateway; <c>Server</c>, <c>Via</c> and <c>X-Powered-By</c> only leak topology.
    /// </remarks>
    private static List<string> CopyResponseHeaders(
        HttpContext context,
        HttpResponseMessage response,
        bool isStreaming)
    {
        var copied = new List<string>();

        foreach (var header in response.Headers)
        {
            if (ShouldSkipResponseHeader(header.Key, isStreaming))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
            copied.Add(header.Key);
        }

        if (response.Content is null)
        {
            return copied;
        }

        foreach (var header in response.Content.Headers)
        {
            if (ShouldSkipResponseHeader(header.Key, isStreaming))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
            copied.Add(header.Key);
        }

        return copied;
    }

    private ErrorBodySnippet? CreateSnippetBufferIfErrorResponse(HttpResponseMessage response)
    {
        var options = errorTracking?.Value;
        if (options is null
            || !options.CaptureUpstreamBodySnippet
            || options.UpstreamBodySnippetBytes <= 0
            || (int)response.StatusCode < StatusCodes.Status400BadRequest
            || HasNonIdentityContentEncoding(response.Content?.Headers))
        {
            // A compressed body is not text on the wire; storing its first bytes would only put
            // binary noise in the Errors tab.
            return null;
        }

        return new ErrorBodySnippet(options.UpstreamBodySnippetBytes);
    }

    private static bool HasNonIdentityContentEncoding(HttpContentHeaders? headers)
    {
        if (headers is null)
        {
            return false;
        }

        foreach (var encoding in headers.ContentEncoding)
        {
            if (!string.IsNullOrWhiteSpace(encoding) &&
                !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keeps the exception behind an outcome for the error record. The outcome alone says "upstream
    /// error"; the exception says "connection refused on 172.26.81.2:2216", which is what an
    /// operator needs.
    /// </summary>
    private static void StashException(HttpContext context, Exception exception) =>
        context.Items[GatewayErrorContextKeys.UpstreamException] = exception;

    private static void StashSnippet(HttpContext context, ErrorBodySnippet? snippet)
    {
        var text = snippet?.ToText();
        if (!string.IsNullOrWhiteSpace(text))
        {
            context.Items[GatewayErrorContextKeys.UpstreamBodySnippet] = text;
        }
    }

    /// <summary>
    /// Fixed-capacity copy of the first bytes of an upstream error body. Bytes past the cap are
    /// dropped, so a runaway error page costs at most the configured snippet size.
    /// </summary>
    private sealed class ErrorBodySnippet(int capacity)
    {
        private readonly byte[] _bytes = new byte[capacity];
        private int _length;

        public void Append(ReadOnlySpan<byte> chunk)
        {
            var room = _bytes.Length - _length;
            if (room <= 0)
            {
                return;
            }

            var take = Math.Min(room, chunk.Length);
            chunk[..take].CopyTo(_bytes.AsSpan(_length));
            _length += take;
        }

        public string ToText() =>
            _length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(_bytes, 0, _length);
    }

    /// <summary>
    /// Undoes <see cref="CopyResponseHeaders"/> when the body phase failed before the response
    /// started, so the gateway error the caller writes next is not decorated with the upstream's
    /// headers.
    /// </summary>
    private static void RemoveCopiedHeadersIfNotStarted(HttpContext context, List<string>? copiedHeaderNames)
    {
        if (copiedHeaderNames is null || context.Response.HasStarted)
        {
            return;
        }

        foreach (var name in copiedHeaderNames)
        {
            context.Response.Headers.Remove(name);
        }
    }

    private static void ApplyStreamingResponseHeaders(HttpContext context)
    {
        context.Response.Headers.Remove("Content-Length");
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static readonly string[] NeverCopiedResponseHeaders =
    [
        // Hop-by-hop: describe the upstream connection, not the client's.
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        // The gateway's own concerns; an upstream value must not overwrite or leak.
        "Set-Cookie",
        "Set-Cookie2",
        "WWW-Authenticate",
        "Server",
        "Via",
        "X-Powered-By",
        "Alt-Svc",
    ];

    private static bool ShouldSkipResponseHeader(string headerName, bool isStreaming)
    {
        foreach (var never in NeverCopiedResponseHeaders)
        {
            if (string.Equals(headerName, never, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (headerName.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return isStreaming &&
               string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase);
    }

    private void RecordTimeToFirstTokenIfNeeded(HttpContext context)
    {
        if (!context.Items.TryGetValue(InferenceForwardingContextKeys.StartedUtc, out var startedValue) ||
            startedValue is not DateTimeOffset startedUtc)
        {
            return;
        }

        if (!context.Items.TryGetValue(InferenceForwardingContextKeys.ModelId, out var modelValue) ||
            modelValue is not string modelId ||
            string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        if (context.Items.ContainsKey(InferenceForwardingContextKeys.TimeToFirstTokenRecorded))
        {
            return;
        }

        context.Items[InferenceForwardingContextKeys.TimeToFirstTokenRecorded] = true;
        var elapsedSeconds = Math.Max(0, (DateTimeOffset.UtcNow - startedUtc).TotalSeconds);
        // Kept on the context so the feed row can carry it; the router reads it at completion.
        context.Items[InferenceForwardingContextKeys.TimeToFirstTokenMs] = elapsedSeconds * 1_000d;
        metricsCollector.RecordTimeToFirstToken(modelId, elapsedSeconds);
    }

    /// <param name="flushEachChunk">
    /// Streaming responses must reach the client chunk by chunk. A buffered response has no such
    /// requirement, so it is left to the server's own flushing rather than paying a flush per read.
    /// </param>
    /// <exception cref="UpstreamBodyReadException">
    /// The upstream read failed with an <see cref="IOException"/> (which includes
    /// <see cref="HttpIOException"/>). Wrapped so the caller can tell an upstream failure from a
    /// client-side write failure, which surfaces as a bare <see cref="IOException"/>.
    /// </exception>
    private static async Task CopyStreamWithFlushAsync(
        Stream source,
        Stream destination,
        ErrorBodySnippet? snippet,
        Action? onFirstByteWritten,
        Action? onChunkForwarded,
        bool flushEachChunk,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var firstByteWritten = false;
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await source
                        .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new UpstreamBodyReadException(ex);
                }

                if (read <= 0)
                {
                    break;
                }

                snippet?.Append(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (flushEachChunk)
                {
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // Rearm only once the bytes are actually with the client: progress, not mere
                // upstream activity, is what proves the response is alive.
                onChunkForwarded?.Invoke();

                if (!firstByteWritten)
                {
                    firstByteWritten = true;
                    onFirstByteWritten?.Invoke();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HasRequestBody(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) ||
        HttpMethods.IsPut(request.Method) ||
        HttpMethods.IsPatch(request.Method) ||
        request.ContentLength is > 0;

    /// <summary>
    /// An <see cref="IOException"/> raised while reading the upstream response body, as opposed to
    /// one raised while writing to the client.
    /// </summary>
    private sealed class UpstreamBodyReadException(IOException inner)
        : Exception("Reading the upstream response body failed.", inner);
}
