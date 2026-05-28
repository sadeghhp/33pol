namespace Pol33.Proxy.Forwarding;

/// <summary>
/// Buffers proxied response bytes (bounded) so SSE usage can be parsed after the stream completes.
/// </summary>
internal sealed class UsageCapturingStream : Stream
{
    private const int MaxBufferBytes = 512 * 1024;

    private readonly Stream _inner;
    private readonly Action<ReadOnlyMemory<byte>> _onComplete;
    private readonly MemoryStream _buffer = new();
    private bool _completed;

    public UsageCapturingStream(Stream inner, Action<ReadOnlyMemory<byte>> onComplete)
    {
        _inner = inner;
        _onComplete = onComplete;
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
        if (chunk.Length == 0 || _buffer.Length >= MaxBufferBytes)
        {
            return;
        }

        var remaining = MaxBufferBytes - (int)_buffer.Length;
        var toWrite = Math.Min(remaining, chunk.Length);
        _buffer.Write(chunk[..toWrite]);
    }

    private void CompleteIfNeeded()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        var length = (int)_buffer.Length;
        if (length == 0)
        {
            _onComplete(ReadOnlyMemory<byte>.Empty);
            return;
        }

        _onComplete(_buffer.GetBuffer().AsMemory(0, length));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteIfNeeded();
            _inner.Dispose();
            _buffer.Dispose();
        }

        base.Dispose(disposing);
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
