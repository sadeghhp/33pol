using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Pol33.Proxy.Parsing;

namespace Pol33.Proxy.Forwarding;

/// <summary>
/// Forwards the buffered request body with the client's <c>model</c> value replaced by the canonical
/// model id, streaming straight from the buffer instead of materialising a rewritten copy.
/// </summary>
/// <remarks>
/// <para>The rewrite it replaces read the body into a <see cref="string"/>, re-parsed it into a
/// <see cref="JsonDocument"/>, wrote it back through a growing <c>MemoryStream</c>, copied that with
/// <c>ToArray()</c>, decoded the result to another string and finally re-encoded it into a
/// <c>StringContent</c>. Measured at ~13x the body size in allocations, essentially all of it on the
/// Large Object Heap, per aliased request — which at the default 25 MB body cap and 64 concurrent
/// forwards per model is several times the memory a gateway pod is given.</para>
///
/// <para>Only the <c>model</c> token itself moves, so everything else — key order, whitespace,
/// escape sequences, non-ASCII content — reaches the upstream byte for byte. The previous rewrite
/// round-tripped the body through UTF-16, which silently replaced malformed UTF-8 with U+FFFD.</para>
///
/// <para>Serialisation is repeatable: the body is seeked from the start each time, so a request that
/// <see cref="HttpClient"/> retries on a fresh connection still sends a complete payload.</para>
/// </remarks>
internal sealed class ModelRewritingHttpContent : HttpContent
{
    private const int CopyBufferBytes = 64 * 1024;

    private readonly Stream _body;
    private readonly JsonValueRange _modelValueRange;
    private readonly byte[] _replacement;

    private ModelRewritingHttpContent(Stream body, JsonValueRange modelValueRange, byte[] replacement)
    {
        _body = body;
        _modelValueRange = modelValueRange;
        _replacement = replacement;
    }

    /// <summary>
    /// Builds the rewritten content, or returns null when <paramref name="modelValueRange"/> does not
    /// describe a range inside <paramref name="body"/> — in which case the caller must forward the
    /// body unchanged rather than send something truncated.
    /// </summary>
    public static ModelRewritingHttpContent? TryCreate(
        Stream body,
        JsonValueRange modelValueRange,
        string canonicalModelId,
        MediaTypeHeaderValue? contentType)
    {
        if (!body.CanSeek ||
            modelValueRange.Start < 0 ||
            modelValueRange.End <= modelValueRange.Start ||
            modelValueRange.End > body.Length)
        {
            return null;
        }

        var content = new ModelRewritingHttpContent(body, modelValueRange, EncodeJsonString(canonicalModelId));
        content.Headers.ContentType = contentType ?? new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        return content;
    }

    /// <summary>Encodes the canonical id as a complete JSON string token, quotes included.</summary>
    private static byte[] EncodeJsonString(string value)
    {
        var encoded = JsonEncodedText.Encode(value).EncodedUtf8Bytes;
        var buffer = new byte[encoded.Length + 2];
        buffer[0] = (byte)'"';
        encoded.CopyTo(buffer.AsSpan(1));
        buffer[^1] = (byte)'"';
        return buffer;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        _body.Position = 0;

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            await CopyExactlyAsync(_body, stream, _modelValueRange.Start, buffer, cancellationToken)
                .ConfigureAwait(false);

            await stream.WriteAsync(_replacement, cancellationToken).ConfigureAwait(false);

            _body.Position = _modelValueRange.End;
            await CopyExactlyAsync(
                    _body,
                    stream,
                    _body.Length - _modelValueRange.End,
                    buffer,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _body.Length - _modelValueRange.Length + _replacement.Length;
        return true;
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long count,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Buffered request body ended before the expected number of bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }
}
