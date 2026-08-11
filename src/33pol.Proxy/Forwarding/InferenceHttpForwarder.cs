using System.Buffers;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
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
    /// <see cref="ForwarderError.RequestTimedOut"/> when headers did not arrive in time (a backend
    /// health signal);
    /// <see cref="ForwarderError.ResponseBodyDestination"/> when a streaming body stalled past the
    /// idle timeout (inconclusive — abandon the probe);
    /// <see cref="ForwarderError.RequestCanceled"/> when the client went away.
    /// </returns>
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
    ILogger<InferenceHttpForwarder> logger) : IInferenceHttpForwarder
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
        var outboundUri = InferenceDestinationBuilder.BuildOutboundUri(
            destinationPrefix,
            context.Request.Path,
            context.Request.QueryString);

        using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), outboundUri);

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
        // returns as soon as headers arrive, so it never reaches the body.
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
            return ForwarderError.RequestTimedOut;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream HTTP request failed for {Method} {Uri}", requestMessage.Method, requestMessage.RequestUri);
            return ForwarderError.Request;
        }
        catch (TaskCanceledException)
        {
            // HttpClient.Timeout (as opposed to our linked token) also surfaces here.
            return ForwarderError.RequestTimedOut;
        }

        using (responseMessage)
        {
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

                CopyResponseHeaders(context, responseMessage, isStreaming);

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

                try
                {
                    await CopyStreamWithFlushAsync(
                            upstreamBody,
                            context.Response.Body,
                            // Time to first token is a streaming notion; a buffered response has no
                            // meaningful first-token moment to report.
                            onFirstByteWritten: isStreaming
                                ? () => RecordTimeToFirstTokenIfNeeded(context)
                                : null,
                            onChunkForwarded: () => idleCts.CancelAfter(timeouts.StreamIdleTimeout),
                            flushEachChunk: isStreaming,
                            idleCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested &&
                                                        !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Upstream stalled for more than {StreamIdleTimeoutSeconds}s while sending the response body for {Uri}",
                        timeouts.StreamIdleTimeout.TotalSeconds,
                        requestMessage.RequestUri);
                    return ForwarderError.ResponseBodyDestination;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ForwarderError.RequestCanceled;
            }
            catch (IOException)
            {
                // Client disconnected while streaming response body.
                return ForwarderError.RequestCanceled;
            }
        }

        return ForwarderError.None;
    }

    private static void CopyResponseHeaders(
        HttpContext context,
        HttpResponseMessage response,
        bool isStreaming)
    {
        foreach (var header in response.Headers)
        {
            if (ShouldSkipResponseHeader(header.Key, isStreaming))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        if (response.Content is null)
        {
            return;
        }

        foreach (var header in response.Content.Headers)
        {
            if (ShouldSkipResponseHeader(header.Key, isStreaming))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private static void ApplyStreamingResponseHeaders(HttpContext context)
    {
        context.Response.Headers.Remove("Content-Length");
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static bool ShouldSkipResponseHeader(string headerName, bool isStreaming)
    {
        if (string.Equals(headerName, "Connection", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "TE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "Trailer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(headerName, "Upgrade", StringComparison.OrdinalIgnoreCase))
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
        metricsCollector.RecordTimeToFirstToken(modelId, elapsedSeconds);
    }

    /// <param name="flushEachChunk">
    /// Streaming responses must reach the client chunk by chunk. A buffered response has no such
    /// requirement, so it is left to the server's own flushing rather than paying a flush per read.
    /// </param>
    private static async Task CopyStreamWithFlushAsync(
        Stream source,
        Stream destination,
        Action? onFirstByteWritten,
        Action? onChunkForwarded,
        bool flushEachChunk,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var firstByteWritten = false;
        try
        {
            int read;
            while ((read = await source
                       .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
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

}
