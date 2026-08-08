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

        CopyAllowedRequestHeaders(httpContext.Request, proxyRequest);

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

        var originalStream = await proxyResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var capturingStream = _isStreaming
            ? new UsageCapturingStream(
                originalStream,
                // Stats carry how much was actually streamed, so a body cut short before its usage
                // frame can still be billed from an estimate rather than recorded as zero. SSE usage
                // arrives in the final frames, so only the tail is meaningful here.
                (_, tail, stats) => _usageCapture!.CaptureFromSseText(
                    Encoding.UTF8.GetString(tail), stats),
                isStreaming: true,
                onCaptureFailed: _usageCapture!.OnCaptureFailed)
            : new UsageCapturingStream(
                originalStream,
                // Head for bodies that fit (exact parse), tail as the fallback for bodies that do
                // not — the trailing usage object is what makes large responses billable at all.
                (head, tail, stats) => _usageCapture!.CaptureFromJsonBody(head, tail, stats),
                isStreaming: false,
                onCaptureFailed: _usageCapture!.OnCaptureFailed);

        proxyResponse.Content = new StreamContent(capturingStream);
        if (contentType is not null)
        {
            proxyResponse.Content.Headers.ContentType = contentType;
        }
    }

    /// <summary>
    /// Client request headers the gateway relays to the upstream.
    /// </summary>
    /// <remarks>
    /// <para>An allowlist, not a copy-everything. The forwarder builds a fresh request and this
    /// transformer never chained to <c>base.TransformRequestAsync</c>, so previously <em>no</em>
    /// client header reached the upstream at all. That silently broke any provider feature carried
    /// by a header — <c>OpenAI-Beta</c>, <c>OpenAI-Organization</c>, provider API versions — with no
    /// diagnostic beyond the feature not working.</para>
    ///
    /// <para>Deliberately excluded: anything that would let a client influence routing, identity or
    /// framing. <c>Authorization</c> and <c>X-API-Key</c> are the gateway's own credential to
    /// replace; <c>Host</c>, <c>Content-Length</c> and <c>Transfer-Encoding</c> belong to the new
    /// request; <c>Accept-Encoding</c> is omitted so upstream bodies arrive uncompressed and stay
    /// parseable for usage capture; forwarding headers (<c>X-Forwarded-*</c>) are not relayed
    /// because a client could otherwise spoof them.</para>
    /// </remarks>
    private static readonly string[] ForwardableRequestHeaders =
    [
        "Accept",
        "Accept-Language",
        "User-Agent",
        "OpenAI-Beta",
        "OpenAI-Organization",
        "OpenAI-Project",
        "anthropic-version",
        "anthropic-beta",
        "X-Stainless-Lang",
        "X-Stainless-Package-Version",
        "X-Stainless-Runtime",
        "X-Stainless-Runtime-Version",
    ];

    private static void CopyAllowedRequestHeaders(HttpRequest source, HttpRequestMessage destination)
    {
        foreach (var name in ForwardableRequestHeaders)
        {
            if (!source.Headers.TryGetValue(name, out var values) || values.Count == 0)
            {
                continue;
            }

            destination.Headers.TryAddWithoutValidation(name, values.ToArray());
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
