using System.Net;
using System.Net.Http.Headers;

namespace Pol33.Proxy.Forwarding;

/// <summary>
/// Forwards a seekable request body, rewinding to the start on every serialisation.
/// </summary>
/// <remarks>
/// <para>The plain <see cref="StreamContent"/> this replaces reads from wherever the stream happens
/// to be positioned and refuses to be read a second time. That makes it the odd one out on this
/// path: <see cref="ModelRewritingHttpContent"/> — used whenever the client addressed the model by
/// an alias — deliberately seeks to zero for exactly this reason, so an aliased request and an
/// unaliased one had different replay behaviour for no reason anyone chose.</para>
///
/// <para><see cref="HttpClient"/> retries a request that failed before anything was written, which
/// is the normal outcome of picking up a pooled connection the upstream has already closed. Whether
/// today's runtime can reach that path with content attached is not something worth depending on:
/// the body is buffered and seekable either way, so making it replayable costs a seek.</para>
/// </remarks>
internal sealed class SeekableStreamContent : HttpContent
{
    private readonly Stream _body;

    private SeekableStreamContent(Stream body) => _body = body;

    /// <summary>
    /// Wraps <paramref name="body"/>, or returns null when it cannot be rewound — in which case the
    /// caller should fall back to <see cref="StreamContent"/> and accept a single read.
    /// </summary>
    public static SeekableStreamContent? TryCreate(Stream body, MediaTypeHeaderValue? contentType)
    {
        if (!body.CanSeek)
        {
            return null;
        }

        var content = new SeekableStreamContent(body);
        if (contentType is not null)
        {
            content.Headers.ContentType = contentType;
        }

        return content;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        _body.Position = 0;
        await _body.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _body.Length;
        return true;
    }
}
