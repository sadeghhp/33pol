using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
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

        byte[]? requestBody = null;
        if (HasRequestBody(context.Request))
        {
            context.Request.Body.Position = 0;
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            requestBody = buffer.ToArray();
            context.Request.Body = new MemoryStream(requestBody);
            context.Request.Body.Position = 0;
            using var json = JsonDocument.Parse(requestBody);
            requestMessage.Content = JsonContent.Create(json.RootElement.Clone());
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

        return ForwarderError.None;
    }

    private static void CopyResponseHeaders(
        HttpContext context,
        HttpResponseMessage response,
        bool isStreaming)
    {
        foreach (var header in response.Headers)
        {
            if (isStreaming && ShouldSkipStreamingResponseHeader(header.Key))
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
            if (isStreaming && ShouldSkipStreamingResponseHeader(header.Key))
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

    private static bool ShouldSkipStreamingResponseHeader(string headerName) =>
        string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(headerName, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase);

    private static async Task CopyStreamWithFlushAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        int read;
        while ((read = await source
                   .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool HasRequestBody(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) ||
        HttpMethods.IsPut(request.Method) ||
        HttpMethods.IsPatch(request.Method) ||
        request.ContentLength is > 0;

}
