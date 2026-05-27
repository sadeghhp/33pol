using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Pol33.Integration.Tests.Support;

/// <summary>
/// Upstream mock that emits the first SSE chunk immediately and delays before the second chunk.
/// Used to verify the gateway forwards streaming bodies incrementally.
/// </summary>
internal sealed class DelayedChunkStreamingHandler : HttpMessageHandler
{
    public const string FirstChunkMarker = "chunk-first";
    public const string SecondChunkMarker = "chunk-second";
    public static readonly TimeSpan InterChunkDelay = TimeSpan.FromMilliseconds(800);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!IsStreamingRequest(body))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DelayedSseStream())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") },
            },
        };
    }

    private static bool IsStreamingRequest(string? body) =>
        body is not null &&
        (body.Contains("\"stream\":true", StringComparison.Ordinal) ||
         body.Contains("\"stream\": true", StringComparison.Ordinal));

    private sealed class DelayedSseStream : Stream
    {
        private int _chunkIndex;
        private byte[]? _pending;
        private int _pendingOffset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_pending is null)
            {
                if (_chunkIndex == 0)
                {
                    _pending = Encoding.UTF8.GetBytes(
                        $"data: {{\"marker\":\"{FirstChunkMarker}\"}}\n\n");
                    _chunkIndex = 1;
                }
                else if (_chunkIndex == 1)
                {
                    await Task.Delay(InterChunkDelay, cancellationToken).ConfigureAwait(false);
                    _pending = Encoding.UTF8.GetBytes(
                        $"data: {{\"marker\":\"{SecondChunkMarker}\"}}\n\ndata: [DONE]\n\n");
                    _chunkIndex = 2;
                }
                else
                {
                    return 0;
                }

                _pendingOffset = 0;
            }

            var remaining = _pending.Length - _pendingOffset;
            var toCopy = Math.Min(remaining, buffer.Length);
            _pending.AsSpan(_pendingOffset, toCopy).CopyTo(buffer.Span);
            _pendingOffset += toCopy;

            if (_pendingOffset >= _pending.Length)
            {
                _pending = null;
                _pendingOffset = 0;
            }

            return toCopy;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
