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
    Task<ForwarderError> SendAsync(
        HttpContext context,
        string modelUrl,
        string? upstreamBearerToken,
        StreamingHttpTransformer transformer,
        bool isStreaming,
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

        var completionOption = isStreaming
            ? HttpCompletionOption.ResponseHeadersRead
            : HttpCompletionOption.ResponseContentRead;

        HttpResponseMessage responseMessage;
        try
        {
            responseMessage = await client
                .SendAsync(requestMessage, completionOption, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ForwarderError.RequestCanceled;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream HTTP request failed for {Method} {Uri}", requestMessage.Method, requestMessage.RequestUri);
            return ForwarderError.Request;
        }
        catch (TaskCanceledException)
        {
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

                    await using var upstreamBody = await responseMessage.Content
                        .ReadAsStreamAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await CopyStreamWithFlushAsync(
                            upstreamBody,
                            context.Response.Body,
                            onFirstByteWritten: () => RecordTimeToFirstTokenIfNeeded(context),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await responseMessage.Content
                        .CopyToAsync(context.Response.Body, cancellationToken)
                        .ConfigureAwait(false);
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

    private static async Task CopyStreamWithFlushAsync(
        Stream source,
        Stream destination,
        Action? onFirstByteWritten,
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
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

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
