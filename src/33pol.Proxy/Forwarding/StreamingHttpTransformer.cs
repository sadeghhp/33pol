using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yarp.ReverseProxy.Forwarder;

namespace Pol33.Proxy.Forwarding;

public sealed class StreamingHttpTransformer : HttpTransformer
{
    private readonly bool _isStreaming;
    private readonly string? _clientModelName;
    private readonly string _canonicalModelId;

    public StreamingHttpTransformer(bool isStreaming, string? clientModelName, string canonicalModelId)
    {
        _isStreaming = isStreaming;
        _clientModelName = clientModelName;
        _canonicalModelId = canonicalModelId;
    }

    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken)
            .ConfigureAwait(false);

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
        if (_isStreaming)
        {
            httpContext.Response.Headers.Remove("Content-Length");
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        }

        return await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken)
            .ConfigureAwait(false);
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
