using System.Net;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pol33.Proxy.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Forwarding;

public sealed class StreamingHttpTransformer : HttpTransformer
{
    private readonly bool _isStreaming;
    private readonly string? _clientModelName;
    private readonly string _canonicalModelId;
    private readonly InferenceUsageCapture? _usageCapture;
    private readonly bool _stripClientAuthHeaders;
    private readonly string? _upstreamBearerToken;

    public StreamingHttpTransformer(
        bool isStreaming,
        string? clientModelName,
        string canonicalModelId,
        InferenceUsageCapture? usageCapture = null,
        bool stripClientAuthHeaders = true,
        string? upstreamBearerToken = null)
    {
        _isStreaming = isStreaming;
        _clientModelName = clientModelName;
        _canonicalModelId = canonicalModelId;
        _usageCapture = usageCapture;
        _stripClientAuthHeaders = stripClientAuthHeaders;
        _upstreamBearerToken = upstreamBearerToken;
    }

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        proxyRequest.RequestUri = InferenceDestinationBuilder.BuildOutboundUri(
            destinationPrefix,
            httpContext.Request.Path,
            httpContext.Request.QueryString);

        if (_stripClientAuthHeaders)
        {
            proxyRequest.Headers.Authorization = null;
            proxyRequest.Headers.Remove("Authorization");
            proxyRequest.Headers.Remove("X-API-Key");
        }

        if (!string.IsNullOrWhiteSpace(_upstreamBearerToken))
        {
            proxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _upstreamBearerToken);
        }

        if (_clientModelName is not null &&
            !string.Equals(_clientModelName, _canonicalModelId, StringComparison.OrdinalIgnoreCase) &&
            httpContext.Request.Body.CanSeek)
        {
            httpContext.Request.Body.Position = 0;
            using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: true);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            httpContext.Request.Body.Position = 0;

            var rewritten = RewriteModelProperty(body, _canonicalModelId);
            proxyRequest.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
            proxyRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
    }

    public override async ValueTask<bool> TransformResponseAsync(
        HttpContext httpContext,
        HttpResponseMessage? proxyResponse,
        CancellationToken cancellationToken)
    {
        if (_usageCapture is not null && proxyResponse?.Content is not null)
        {
            await PrepareUsageCapturingContentAsync(proxyResponse, cancellationToken).ConfigureAwait(false);
        }

        // Response headers and body copy are handled by InferenceHttpForwarder for both
        // streaming and non-streaming responses.
        // Calling YARP's default response transform here can re-apply transfer headers
        // before we copy the response body ourselves.
        _ = httpContext;
        _ = proxyResponse;
        _ = cancellationToken;
        return true;
    }

    private async Task PrepareUsageCapturingContentAsync(
        HttpResponseMessage proxyResponse,
        CancellationToken cancellationToken)
    {
        var contentType = proxyResponse.Content!.Headers.ContentType;

        if (_isStreaming)
        {
            var originalStream = await proxyResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var capturingStream = new UsageCapturingStream(
                originalStream,
                sseText => _usageCapture!.CaptureFromSseText(sseText));
            proxyResponse.Content = new StreamContent(capturingStream);
            if (contentType is not null)
            {
                proxyResponse.Content.Headers.ContentType = contentType;
            }

            return;
        }

        var body = await proxyResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        _usageCapture!.CaptureFromJsonBody(body);
        proxyResponse.Content = new ByteArrayContent(body);
        if (contentType is not null)
        {
            proxyResponse.Content.Headers.ContentType = contentType;
        }
    }

    public static string RewriteModelProperty(string json, string canonicalModelId)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "model", StringComparison.Ordinal))
                {
                    writer.WriteString("model", canonicalModelId);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
