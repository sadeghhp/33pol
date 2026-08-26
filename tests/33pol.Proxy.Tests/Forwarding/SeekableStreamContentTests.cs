using System.Net.Http.Headers;
using System.Text;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class SeekableStreamContentTests
{
    /// <summary>
    /// The point of the type: serialising twice sends the same bytes twice.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="StreamContent"/> reads from wherever the stream is left and refuses a
    /// second read, so a request replayed onto a fresh connection would not send the same body.
    /// </remarks>
    [Fact]
    public async Task SerializeToStreamAsync_CalledTwice_SendsTheWholeBodyEachTime()
    {
        const string body = """{"model":"gpt","stream":true}""";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var content = SeekableStreamContent.TryCreate(source, new MediaTypeHeaderValue("application/json"));
        content.Should().NotBeNull();

        (await content!.ReadAsStringAsync()).Should().Be(body);
        (await content.ReadAsStringAsync()).Should().Be(body, "the content must be replayable");
    }

    [Fact]
    public async Task SerializeToStreamAsync_FromANonZeroPosition_StillSendsTheWholeBody()
    {
        const string body = """{"model":"gpt"}""";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(body)) { Position = 5 };
        var content = SeekableStreamContent.TryCreate(source, contentType: null);

        (await content!.ReadAsStringAsync()).Should().Be(body);
    }

    [Fact]
    public void TryCreate_NonSeekableBody_ReturnsNullSoTheCallerCanFallBack()
    {
        using var source = new NonSeekableStream();

        SeekableStreamContent.TryCreate(source, contentType: null).Should().BeNull();
    }

    [Fact]
    public void ContentLength_IsTheWholeBody_NotWhatIsLeftFromThePosition()
    {
        using var source = new MemoryStream(new byte[128]) { Position = 100 };
        var content = SeekableStreamContent.TryCreate(source, contentType: null);

        content!.Headers.ContentLength.Should().Be(128);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
