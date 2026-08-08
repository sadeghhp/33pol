using System.Buffers;

namespace Pol33.Proxy.Forwarding;

/// <summary>
/// Receives the retained window of a proxied response body once the stream completes.
/// </summary>
/// <param name="head">
/// Leading bytes, retained only for non-streaming responses and only up to
/// <see cref="UsageCapturingStream.MaxHeadBytes"/>. Empty for streaming responses.
/// </param>
/// <param name="tail">Trailing bytes, always retained, up to <see cref="UsageCapturingStream.TailBufferBytes"/>.</param>
/// <param name="stats">What was observed of the whole body, independent of the retained window.</param>
internal delegate void UsageCaptureCallback(
    ReadOnlySpan<byte> head,
    ReadOnlySpan<byte> tail,
    UsageCaptureStats stats);

/// <summary>
/// Buffers proxied response bytes (bounded) so usage can be parsed after the stream completes.
/// </summary>
/// <remarks>
/// <para>The <em>tail</em> is retained for every response, because that is where the token counts
/// live in both shapes the gateway proxies: the terminal <c>usage</c> SSE frame of a stream, and the
/// trailing <c>usage</c> object of an OpenAI-shaped JSON body.</para>
///
/// <para>For non-streaming responses the <em>head</em> is retained as well, so a body that fits
/// inside the cap can be parsed exactly as a whole document rather than recovered from a fragment.
/// Retaining only the head was a billing hole: a response larger than the cap — which every batch
/// embeddings response is — arrived at the parser truncated, failed to parse, and recorded no usage
/// at all, so the request went unbilled. Keeping the tail as well means the <c>usage</c> object is
/// always inside the retained window regardless of body size.</para>
///
/// <para>Buffers are rented from <see cref="ArrayPool{T}"/>. They were previously allocated per
/// request at 512 KB, which is well past the 85 KB Large Object Heap threshold: every streaming
/// request put a fresh LOH array in gen-2, and a wrapped ring allocated a second one to reorder it.
/// The tail is also far smaller now — a terminal <c>usage</c> frame or object is a few hundred
/// bytes, so 32 KB is ample.</para>
/// </remarks>
internal sealed class UsageCapturingStream : Stream
{
    /// <summary>
    /// Retained tail. Only needs to hold the final frames (streaming) or the trailing <c>usage</c>
    /// object (non-streaming); sized with generous headroom over either.
    /// </summary>
    internal const int TailBufferBytes = 32 * 1024;

    /// <summary>Cap on the head buffered for non-streaming responses, so a huge body stays bounded.</summary>
    internal const int MaxHeadBytes = 256 * 1024;

    private readonly Stream _inner;
    private readonly UsageCaptureCallback _onComplete;
    private readonly Action<Exception>? _onCaptureFailed;
    private readonly bool _isStreaming;

    // Progress counters, tracked over the whole stream rather than the retained window. When the
    // terminal usage frame never arrives (client disconnect), the frame count is what lets the
    // gateway record an estimate instead of silently billing zero.
    private long _frameCount;
    private long _totalBytes;
    private bool _lastByteWasNewline;

    // Rented arrays may be larger than requested, so the logical capacity is tracked separately —
    // using buffer.Length for the ring arithmetic would silently change the retained window.
    private readonly byte[] _tail;
    private readonly int _tailCapacity;
    private byte[]? _head;
    private int _headLength;

    private long _tailWritten;
    private bool _completed;
    private bool _disposed;

    /// <param name="isStreaming">
    /// Streaming responses skip head retention (their body is unbounded and the head is worthless
    /// for usage) and count SSE frames; non-streaming responses retain both ends.
    /// </param>
    public UsageCapturingStream(
        Stream inner,
        UsageCaptureCallback onComplete,
        bool isStreaming = false,
        Action<Exception>? onCaptureFailed = null)
    {
        _inner = inner;
        _onComplete = onComplete;
        _onCaptureFailed = onCaptureFailed;
        _isStreaming = isStreaming;
        _tail = ArrayPool<byte>.Shared.Rent(TailBufferBytes);
        _tailCapacity = TailBufferBytes;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Append(buffer.AsSpan(offset, read));
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Append(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Append(buffer.Span[..read]);
        return read;
    }

    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0 || _completed)
        {
            return;
        }

        _totalBytes += chunk.Length;

        if (_isStreaming)
        {
            CountFrames(chunk);
        }
        else
        {
            AppendHead(chunk);
        }

        AppendTail(chunk);
    }

    /// <summary>
    /// Counts SSE frame terminators (a blank line, i.e. two consecutive newlines) as they stream
    /// past. Counted over the whole body rather than the retained tail, and carried across chunk
    /// boundaries via <see cref="_lastByteWasNewline"/>, so a frame split between two reads is still
    /// counted exactly once.
    /// </summary>
    private void CountFrames(ReadOnlySpan<byte> chunk)
    {
        foreach (var b in chunk)
        {
            if (b == (byte)'\n')
            {
                if (_lastByteWasNewline)
                {
                    _frameCount++;
                    _lastByteWasNewline = false;
                    continue;
                }

                _lastByteWasNewline = true;
                continue;
            }

            // Carriage returns are part of the terminator on CRLF streams, not content.
            if (b != (byte)'\r')
            {
                _lastByteWasNewline = false;
            }
        }
    }

    /// <summary>Grows the head buffer geometrically from the pool, capped at <see cref="MaxHeadBytes"/>.</summary>
    private void AppendHead(ReadOnlySpan<byte> chunk)
    {
        if (_headLength >= MaxHeadBytes)
        {
            return;
        }

        var toWrite = Math.Min(MaxHeadBytes - _headLength, chunk.Length);
        var required = _headLength + toWrite;

        if (_head is null)
        {
            _head = ArrayPool<byte>.Shared.Rent(Math.Max(required, 4 * 1024));
        }
        else if (_head.Length < required)
        {
            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(required, _head.Length * 2));
            _head.AsSpan(0, _headLength).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_head);
            _head = grown;
        }

        chunk[..toWrite].CopyTo(_head.AsSpan(_headLength));
        _headLength += toWrite;
    }

    private void AppendTail(ReadOnlySpan<byte> chunk)
    {
        var tail = _tail.AsSpan(0, _tailCapacity);
        if (chunk.Length >= _tailCapacity)
        {
            // Only the final TailBufferBytes bytes can ever be retained.
            chunk[^_tailCapacity..].CopyTo(tail);
            _tailWritten = _tailCapacity; // ring is full, aligned at index 0
            return;
        }

        var pos = (int)(_tailWritten % _tailCapacity);
        var firstRun = Math.Min(chunk.Length, _tailCapacity - pos);
        chunk[..firstRun].CopyTo(tail[pos..]);
        if (firstRun < chunk.Length)
        {
            chunk[firstRun..].CopyTo(tail);
        }

        _tailWritten += chunk.Length;
    }

    /// <summary>
    /// Invokes the completion callback with the retained head and the retained tail in
    /// oldest-to-newest order. Reordering a wrapped ring needs a scratch buffer; it is rented and
    /// returned rather than allocated, and the callback consumes both spans synchronously.
    /// </summary>
    private void InvokeWithSnapshot()
    {
        var stats = new UsageCaptureStats(_frameCount, _totalBytes);
        var head = _headLength == 0 || _head is null
            ? ReadOnlySpan<byte>.Empty
            : _head.AsSpan(0, _headLength);

        if (_tailWritten == 0)
        {
            _onComplete(head, ReadOnlySpan<byte>.Empty, stats);
            return;
        }

        if (_tailWritten < _tailCapacity)
        {
            // Never wrapped: bytes are in order at [0.._tailWritten).
            _onComplete(head, _tail.AsSpan(0, (int)_tailWritten), stats);
            return;
        }

        var start = (int)(_tailWritten % _tailCapacity);
        if (start == 0)
        {
            _onComplete(head, _tail.AsSpan(0, _tailCapacity), stats);
            return;
        }

        var ordered = ArrayPool<byte>.Shared.Rent(_tailCapacity);
        try
        {
            _tail.AsSpan(start, _tailCapacity - start).CopyTo(ordered);
            _tail.AsSpan(0, start).CopyTo(ordered.AsSpan(_tailCapacity - start));
            _onComplete(head, ordered.AsSpan(0, _tailCapacity), stats);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(ordered);
        }
    }

    private void CompleteIfNeeded()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        InvokeWithSnapshot();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            // Usage capture is best-effort accounting that runs while the response is being torn
            // down — the body has already reached the client. An exception escaping here would fault
            // a request that from the caller's perspective already succeeded, so it is contained and
            // surfaced through the capture callback's own metrics instead.
            try
            {
                CompleteIfNeeded();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _onCaptureFailed?.Invoke(ex);
            }
            finally
            {
                ReturnBuffers();
            }

            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ReturnBuffers()
    {
        // Guarded by _disposed so each rented array is returned exactly once, even under a double
        // dispose (Stream.DisposeAsync also routes here).
        if (_head is not null)
        {
            ArrayPool<byte>.Shared.Return(_head);
            _head = null;
        }

        ArrayPool<byte>.Shared.Return(_tail);
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
