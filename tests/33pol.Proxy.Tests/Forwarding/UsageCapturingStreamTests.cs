using System.Buffers;
using System.Text;
using Pol33.Core.Usage;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

/// <summary>
/// The capture buffer is on the response teardown path of every proxied request, so it must be
/// bounded, pooled, and incapable of faulting a response that has already been delivered.
/// </summary>
public sealed class UsageCapturingStreamTests
{
    private const int TailBufferBytes = UsageCapturingStream.TailBufferBytes;

    private const int MaxHeadBytes = UsageCapturingStream.MaxHeadBytes;

    /// <summary>
    /// Yields the window a consumer would parse: the head when it holds the whole body, otherwise
    /// the tail — which is the recovery path that makes oversized bodies billable.
    /// </summary>
    private static Stream Create(
        Stream inner,
        Action<ReadOnlyMemory<byte>> onComplete,
        bool retainTail = false,
        Action<Exception>? onCaptureFailed = null) =>
        new UsageCapturingStream(
            inner,
            (head, tail, stats) => onComplete(
                !retainTail && !head.IsEmpty && stats.TotalBytes <= head.Length
                    ? head.ToArray()
                    : tail.ToArray()),
            retainTail,
            onCaptureFailed);

    /// <summary>Overload for tests that assert on the streamed-progress statistics.</summary>
    private static Stream CreateWithStats(
        Stream inner,
        Action<ReadOnlyMemory<byte>, UsageCaptureStats> onComplete,
        bool retainTail = true) =>
        new UsageCapturingStream(
            inner,
            (_, tail, stats) => onComplete(tail.ToArray(), stats),
            retainTail);

    [Fact]
    public async Task NonStreaming_RetainsHead_AndParsesUsage()
    {
        const string body = """{"usage":{"prompt_tokens":11,"completion_tokens":4}}""";
        ParsedUsage parsed = default;

        await using (var stream = Create(
            new MemoryStream(Encoding.UTF8.GetBytes(body)),
            captured => parsed = UsageJsonParser.Parse(captured.Span)))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        parsed.Kind.Should().Be(UsageParseKind.Split);
        parsed.PromptTokens.Should().Be(11);
        parsed.CompletionTokens.Should().Be(4);
    }

    /// <summary>
    /// The reason the streaming path retains the tail: the terminal usage frame arrives last, so a
    /// head buffer would drop it on any response larger than the buffer and under-bill the request.
    /// </summary>
    [Fact]
    public async Task Streaming_TerminalUsageFrameSurvivesAMuchLargerBody()
    {
        var sse = new StringBuilder();
        // Comfortably larger than the retained tail, so the ring definitely wraps.
        while (sse.Length < TailBufferBytes * 3)
        {
            sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"filler filler filler\"}}]}\n\n");
        }

        sse.Append("data: {\"usage\":{\"prompt_tokens\":123,\"completion_tokens\":456}}\n\n");
        sse.Append("data: [DONE]\n\n");

        ParsedUsage parsed = default;
        await using (var stream = Create(
            new MemoryStream(Encoding.UTF8.GetBytes(sse.ToString())),
            captured => parsed = UsageJsonParser.ParseSseText(Encoding.UTF8.GetString(captured.Span)),
            retainTail: true))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        parsed.Kind.Should().Be(UsageParseKind.Split);
        parsed.PromptTokens.Should().Be(123);
        parsed.CompletionTokens.Should().Be(456);
    }

    /// <summary>A single write larger than the whole ring must still leave the last bytes retained.</summary>
    [Fact]
    public async Task Streaming_SingleChunkLargerThanTheRing_RetainsTheFinalBytes()
    {
        var payload = new string('x', TailBufferBytes * 2) +
                      "\ndata: {\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":8}}\n";

        ParsedUsage parsed = default;
        await using (var stream = Create(
            new SingleReadStream(Encoding.UTF8.GetBytes(payload)),
            captured => parsed = UsageJsonParser.ParseSseText(Encoding.UTF8.GetString(captured.Span)),
            retainTail: true))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        parsed.PromptTokens.Should().Be(7);
        parsed.CompletionTokens.Should().Be(8);
    }

    [Fact]
    public async Task Streaming_RetainedWindowIsBounded()
    {
        var captured = -1;

        await using (var stream = Create(
            new MemoryStream(Encoding.UTF8.GetBytes(new string('y', TailBufferBytes * 5))),
            snapshot => captured = snapshot.Length,
            retainTail: true))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        captured.Should().Be(TailBufferBytes);
    }

    [Fact]
    public async Task NonStreaming_HeadGrowthIsBounded()
    {
        var capturedHead = -1;
        var capturedTail = -1;

        await using (var stream = new UsageCapturingStream(
            new MemoryStream(Encoding.UTF8.GetBytes(new string('z', MaxHeadBytes * 3))),
            (head, tail, _) =>
            {
                capturedHead = head.Length;
                capturedTail = tail.Length;
            },
            isStreaming: false))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        capturedHead.Should().Be(MaxHeadBytes);

        // The tail is retained for non-streaming bodies too. Keeping only the head meant any
        // response larger than the cap reached the parser truncated, failed to parse, and recorded
        // no usage — so it was never billed. The trailing usage object is always in this window.
        capturedTail.Should().Be(TailBufferBytes);
    }

    /// <summary>
    /// The core safety property: usage capture runs during disposal, after the body has reached the
    /// client. A throwing callback must be contained and reported, never propagated.
    /// </summary>
    [Fact]
    public async Task Dispose_WhenCaptureThrows_DoesNotPropagate()
    {
        Exception? reported = null;

        var stream = Create(
            new MemoryStream("body"u8.ToArray()),
            _ => throw new InvalidOperationException("capture exploded"),
            retainTail: false,
            onCaptureFailed: ex => reported = ex);

        await stream.CopyToAsync(Stream.Null);

        var act = () => stream.Dispose();

        act.Should().NotThrow();
        reported.Should().BeOfType<InvalidOperationException>();
        reported!.Message.Should().Be("capture exploded");
    }

    [Fact]
    public async Task DisposeAsync_WhenCaptureThrows_DoesNotPropagate()
    {
        Exception? reported = null;

        var stream = Create(
            new MemoryStream("body"u8.ToArray()),
            _ => throw new InvalidOperationException("capture exploded"),
            retainTail: true,
            onCaptureFailed: ex => reported = ex);

        await stream.CopyToAsync(Stream.Null);

        var act = async () => await stream.DisposeAsync();

        await act.Should().NotThrowAsync();
        reported.Should().NotBeNull();
    }

    /// <summary>
    /// Rented arrays must be returned exactly once. Returning twice corrupts the shared pool for the
    /// whole process, so double disposal has to be a no-op.
    /// </summary>
    [Fact]
    public async Task DoubleDispose_IsSafe_AndCapturesOnlyOnce()
    {
        var captures = 0;

        var stream = Create(
            new MemoryStream("data: {\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}\n"u8.ToArray()),
            _ => captures++,
            retainTail: true);

        await stream.CopyToAsync(Stream.Null);

        var act = () =>
        {
            stream.Dispose();
            stream.Dispose();
            stream.Dispose();
        };

        act.Should().NotThrow();
        captures.Should().Be(1);
    }

    /// <summary>
    /// Proves the buffers really come from the shared pool and go back to it: after disposal the
    /// pool hands the same array out again.
    /// </summary>
    [Fact]
    public async Task Dispose_ReturnsRentedBuffersToThePool()
    {
        // Drain any array the pool is already holding for this bucket so the comparison is meaningful.
        var probe = ArrayPool<byte>.Shared.Rent(TailBufferBytes);
        ArrayPool<byte>.Shared.Return(probe);

        var stream = Create(
            new MemoryStream("x"u8.ToArray()),
            _ => { },
            retainTail: true);

        await stream.CopyToAsync(Stream.Null);
        stream.Dispose();

        var rented = ArrayPool<byte>.Shared.Rent(TailBufferBytes);
        try
        {
            rented.Length.Should().BeGreaterThanOrEqualTo(TailBufferBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    [Fact]
    public async Task EmptyBody_CapturesEmptySnapshot()
    {
        var length = -1;

        await using (var stream = Create(new MemoryStream([]), snapshot => length = snapshot.Length))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        length.Should().Be(0);
    }

    /// <summary>
    /// A stream cut short before its terminal usage frame must still report how much was streamed,
    /// so the request can be billed from an estimate instead of recorded as zero usage — which is
    /// what made disconnect-before-completion free inference.
    /// </summary>
    [Fact]
    public async Task Streaming_TruncatedBody_ReportsFramesActuallyStreamed()
    {
        var sse = string.Concat(Enumerable.Repeat(
            "data: {\"choices\":[{\"delta\":{\"content\":\"tok\"}}]}\n\n", 12));

        UsageCaptureStats stats = default;
        await using (var stream = CreateWithStats(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            (_, s) => stats = s))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        stats.FrameCount.Should().Be(12);
        stats.ProducedOutput.Should().BeTrue();
    }

    /// <summary>Frames split across read boundaries must be counted exactly once.</summary>
    [Fact]
    public async Task Streaming_FramesSplitAcrossReads_AreCountedOnce()
    {
        var sse = string.Concat(Enumerable.Repeat(
            "data: {\"choices\":[{\"delta\":{\"content\":\"tok\"}}]}\n\n", 20));

        UsageCaptureStats stats = default;
        await using (var stream = CreateWithStats(
            new ChunkedStream(Encoding.UTF8.GetBytes(sse), chunkSize: 7),
            (_, s) => stats = s))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        stats.FrameCount.Should().Be(20, "the terminator carry-over must survive chunk boundaries");
    }

    /// <summary>
    /// A cancellation that produced nothing must not look like output, or the gateway would
    /// fabricate usage for a request the upstream never answered.
    /// </summary>
    [Fact]
    public async Task Streaming_EmptyBody_ReportsNoOutput()
    {
        UsageCaptureStats stats = default;
        await using (var stream = CreateWithStats(new MemoryStream([]), (_, s) => stats = s))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        stats.ProducedOutput.Should().BeFalse();
        stats.FrameCount.Should().Be(0);
    }

    /// <summary>Frame counting spans the whole body, not just the retained tail window.</summary>
    [Fact]
    public async Task Streaming_FrameCountCoversTheWholeBodyNotJustTheRetainedTail()
    {
        var frame = "data: {\"choices\":[{\"delta\":{\"content\":\"filler filler\"}}]}\n\n";
        var frames = (TailBufferBytes * 3 / frame.Length) + 10;
        var sse = string.Concat(Enumerable.Repeat(frame, frames));

        UsageCaptureStats stats = default;
        await using (var stream = CreateWithStats(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            (_, s) => stats = s))
        {
            await stream.CopyToAsync(Stream.Null);
        }

        stats.FrameCount.Should().Be(frames);
    }

    /// <summary>Emits the payload in fixed-size chunks to force frame terminators across boundaries.</summary>
    private sealed class ChunkedStream(byte[] payload, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var toCopy = Math.Min(Math.Min(chunkSize, count), payload.Length - _position);
            if (toCopy <= 0)
            {
                return 0;
            }

            payload.AsSpan(_position, toCopy).CopyTo(buffer.AsSpan(offset));
            _position += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var toCopy = Math.Min(Math.Min(chunkSize, buffer.Length), payload.Length - _position);
            if (toCopy <= 0)
            {
                return ValueTask.FromResult(0);
            }

            payload.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Returns everything in one read, to exercise the oversized-single-chunk path.</summary>
    private sealed class SingleReadStream(byte[] payload) : Stream
    {
        private bool _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read)
            {
                return 0;
            }

            _read = true;
            var toCopy = Math.Min(count, payload.Length);
            payload.AsSpan(0, toCopy).CopyTo(buffer.AsSpan(offset));
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                return ValueTask.FromResult(0);
            }

            _read = true;
            var toCopy = Math.Min(buffer.Length, payload.Length);
            payload.AsSpan(0, toCopy).CopyTo(buffer.Span);
            return ValueTask.FromResult(toCopy);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
