using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

        HttpResponseMessage responseMessage;
        try
        {
            responseMessage = await client
                .SendAsync(requestMessage, cancellationToken)
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

            var transformBody = await transformer
                .TransformResponseAsync(context, responseMessage, cancellationToken)
                .ConfigureAwait(false);

            if (transformBody && responseMessage.Content is not null)
            {
                await responseMessage.Content
                    .CopyToAsync(context.Response.Body, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return ForwarderError.None;
    }

    private static bool HasRequestBody(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) ||
        HttpMethods.IsPut(request.Method) ||
        HttpMethods.IsPatch(request.Method) ||
        request.ContentLength is > 0;

}
