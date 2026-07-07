namespace Pol33.Proxy.Forwarding;

/// <summary>
/// Buffers proxied response bytes (bounded) so usage can be parsed after the stream completes.
/// For non-streaming JSON responses the leading bytes are retained (the whole small body fits).
/// For streaming SSE responses the <em>trailing</em> bytes are retained instead, because the
/// terminal <c>usage</c> chunk arrives at the end of the stream — keeping the head would drop it
/// on any response larger than the buffer and silently under-bill the request.
/// </summary>
internal sealed class UsageCapturingStream : Stream
{
    private const int MaxBufferBytes = 512 * 1024;

    private readonly Stream _inner;
    private readonly Action<ReadOnlyMemory<byte>> _onComplete;
    private readonly bool _retainTail;
    private readonly MemoryStream? _head;
    private readonly byte[]? _tail;
    private long _tailWritten;
    private bool _completed;

    public UsageCapturingStream(
        Stream inner,
        Action<ReadOnlyMemory<byte>> onComplete,
        bool retainTail = false)
    {
        _inner = inner;
        _onComplete = onComplete;
        _retainTail = retainTail;
        if (retainTail)
        {
            _tail = new byte[MaxBufferBytes];
        }
        else
        {
            _head = new MemoryStream();
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
        if (chunk.Length == 0)
        {
            return;
        }

        if (_retainTail)
        {
            AppendTail(chunk);
            return;
        }

        if (_head!.Length >= MaxBufferBytes)
        {
            return;
        }

        var remaining = MaxBufferBytes - (int)_head.Length;
        var toWrite = Math.Min(remaining, chunk.Length);
        _head.Write(chunk[..toWrite]);
    }

    private void AppendTail(ReadOnlySpan<byte> chunk)
    {
        var tail = _tail!;
        if (chunk.Length >= tail.Length)
        {
            // Only the final MaxBufferBytes bytes can ever be retained.
            chunk[^tail.Length..].CopyTo(tail);
            _tailWritten = tail.Length; // ring is full, aligned at index 0
            return;
        }

        var pos = (int)(_tailWritten % tail.Length);
        var firstRun = Math.Min(chunk.Length, tail.Length - pos);
        chunk[..firstRun].CopyTo(tail.AsSpan(pos));
        if (firstRun < chunk.Length)
        {
            chunk[firstRun..].CopyTo(tail.AsSpan(0));
        }

        _tailWritten += chunk.Length;
    }

    private ReadOnlyMemory<byte> SnapshotBuffer()
    {
        if (!_retainTail)
        {
            var length = (int)_head!.Length;
            return length == 0
                ? ReadOnlyMemory<byte>.Empty
                : _head.GetBuffer().AsMemory(0, length);
        }

        var tail = _tail!;
        if (_tailWritten == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (_tailWritten < tail.Length)
        {
            // Never wrapped: bytes are in order at [0.._tailWritten).
            return tail.AsMemory(0, (int)_tailWritten);
        }

        var start = (int)(_tailWritten % tail.Length);
        if (start == 0)
        {
            return tail.AsMemory(0, tail.Length);
        }

        // Wrapped: reassemble oldest-to-newest across the ring boundary.
        var ordered = new byte[tail.Length];
        tail.AsSpan(start).CopyTo(ordered);
        tail.AsSpan(0, start).CopyTo(ordered.AsSpan(tail.Length - start));
        return ordered;
    }

    private void CompleteIfNeeded()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _onComplete(SnapshotBuffer());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteIfNeeded();
            _inner.Dispose();
            _head?.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
