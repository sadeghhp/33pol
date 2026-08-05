using System.Buffers;

namespace Pol33.Proxy.Forwarding;

/// <summary>
/// Buffers proxied response bytes (bounded) so usage can be parsed after the stream completes.
/// For non-streaming JSON responses the leading bytes are retained (the whole small body fits).
/// For streaming SSE responses the <em>trailing</em> bytes are retained instead, because the
/// terminal <c>usage</c> chunk arrives at the end of the stream — keeping the head would drop it
/// on any response larger than the buffer and silently under-bill the request.
/// </summary>
/// <remarks>
/// Buffers are rented from <see cref="ArrayPool{T}"/>. They were previously allocated per request at
/// 512 KB, which is well past the 85 KB Large Object Heap threshold: every streaming request put a
/// fresh LOH array in gen-2, and a wrapped ring allocated a second one to reorder it. The tail is
/// also far smaller now — a terminal SSE usage frame is a few hundred bytes, so 32 KB is ample.
/// </remarks>
internal sealed class UsageCapturingStream : Stream
{
    /// <summary>
    /// Retained tail for streaming responses. Only needs to hold the final SSE frames; sized with
    /// generous headroom over a realistic terminal <c>usage</c> chunk.
    /// </summary>
    internal const int TailBufferBytes = 32 * 1024;

    /// <summary>Cap on the head buffered for non-streaming responses, so a huge body stays bounded.</summary>
    internal const int MaxHeadBytes = 256 * 1024;

    private readonly Stream _inner;
    private readonly Action<ReadOnlyMemory<byte>, UsageCaptureStats> _onComplete;
    private readonly Action<Exception>? _onCaptureFailed;
    private readonly bool _retainTail;

    // Progress counters, tracked over the whole stream rather than the retained window. When the
    // terminal usage frame never arrives (client disconnect), the frame count is what lets the
    // gateway record an estimate instead of silently billing zero.
    private long _frameCount;
    private long _totalBytes;
    private bool _lastByteWasNewline;

    // Rented arrays may be larger than requested, so the logical capacity is tracked separately —
    // using buffer.Length for the ring arithmetic would silently change the retained window.
    private readonly byte[]? _tail;
    private readonly int _tailCapacity;
    private byte[]? _head;
    private int _headLength;

    private long _tailWritten;
    private bool _completed;
    private bool _disposed;

    public UsageCapturingStream(
        Stream inner,
        Action<ReadOnlyMemory<byte>, UsageCaptureStats> onComplete,
        bool retainTail = false,
        Action<Exception>? onCaptureFailed = null)
    {
        _inner = inner;
        _onComplete = onComplete;
        _onCaptureFailed = onCaptureFailed;
        _retainTail = retainTail;
        if (retainTail)
        {
            _tail = ArrayPool<byte>.Shared.Rent(TailBufferBytes);
            _tailCapacity = TailBufferBytes;
        }
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

        if (_retainTail)
        {
            CountFrames(chunk);
            AppendTail(chunk);
            return;
        }

        AppendHead(chunk);
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
        var tail = _tail!.AsSpan(0, _tailCapacity);
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
    /// Returns the retained bytes oldest-to-newest. Reordering a wrapped ring needs a scratch buffer;
    /// it is rented and returned rather than allocated, and the callback consumes it synchronously.
    /// </summary>
    private void WithSnapshot(Action<ReadOnlyMemory<byte>, UsageCaptureStats> onComplete)
    {
        var stats = new UsageCaptureStats(_frameCount, _totalBytes);
        void consume(ReadOnlyMemory<byte> snapshot) => onComplete(snapshot, stats);

        if (!_retainTail)
        {
            consume(_headLength == 0 || _head is null
                ? ReadOnlyMemory<byte>.Empty
                : _head.AsMemory(0, _headLength));
            return;
        }

        if (_tailWritten == 0)
        {
            consume(ReadOnlyMemory<byte>.Empty);
            return;
        }

        if (_tailWritten < _tailCapacity)
        {
            // Never wrapped: bytes are in order at [0.._tailWritten).
            consume(_tail!.AsMemory(0, (int)_tailWritten));
            return;
        }

        var start = (int)(_tailWritten % _tailCapacity);
        if (start == 0)
        {
            consume(_tail!.AsMemory(0, _tailCapacity));
            return;
        }

        var ordered = ArrayPool<byte>.Shared.Rent(_tailCapacity);
        try
        {
            _tail!.AsSpan(start, _tailCapacity - start).CopyTo(ordered);
            _tail.AsSpan(0, start).CopyTo(ordered.AsSpan(_tailCapacity - start));
            consume(ordered.AsMemory(0, _tailCapacity));
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
        WithSnapshot(_onComplete);
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

        if (_tail is not null)
        {
            ArrayPool<byte>.Shared.Return(_tail);
        }
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
