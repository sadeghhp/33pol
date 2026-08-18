using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Pol33.Proxy.Parsing;

namespace Pol33.Proxy.Tests.Parsing;

public sealed class InferenceRequestParserTests
{
    [Fact]
    public async Task ParseAsync_StreamTrue_DetectsStreamingFlag()
    {
        await using var body = new MemoryStream(
            Encoding.UTF8.GetBytes("""{"model":"gpt","stream":true}"""));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.Model.Should().Be("gpt");
        info.Stream.Should().BeTrue();
    }

    [Fact]
    public async Task ParseAsync_StreamOmitted_DefaultsFalse()
    {
        await using var body = new MemoryStream(
            Encoding.UTF8.GetBytes("""{"model":"gpt"}"""));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.Stream.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"model":"gpt","max_tokens":256}""", 256L)]
    [InlineData("""{"model":"gpt","max_completion_tokens":512}""", 512L)]
    // max_tokens wins when both are present, matching the order the parser tries them in.
    [InlineData("""{"model":"gpt","max_completion_tokens":512,"max_tokens":256}""", 256L)]
    // A value beyond Int32 is a real ceiling, not a reason to fall back to the default reservation.
    [InlineData("""{"model":"gpt","max_tokens":3000000000}""", 3_000_000_000L)]
    public async Task ParseAsync_MaxTokens_IsCaptured(string json, long expected)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.MaxTokens.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"model":"gpt"}""")]
    [InlineData("""{"model":"gpt","max_tokens":null}""")]
    [InlineData("""{"model":"gpt","max_tokens":"256"}""")]
    [InlineData("""{"model":"gpt","max_tokens":0}""")]
    [InlineData("""{"model":"gpt","max_tokens":1.5}""")]
    public async Task ParseAsync_MaxTokensUnusable_IsNull(string json)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.MaxTokens.Should().BeNull();
    }

    /// <summary>
    /// Only top-level properties route the request. A "model" or "stream" key inside a message is
    /// content, not configuration.
    /// </summary>
    [Fact]
    public async Task ParseAsync_NestedPropertiesOfTheSameName_AreIgnored()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"messages":[{"role":"user","model":"nested","stream":true}],"model":"gpt"}"""));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.Model.Should().Be("gpt");
        info.Stream.Should().BeFalse();
    }

    /// <summary>
    /// The gateway authorises and bills on the parsed value but forwards the raw bytes, and most
    /// upstreams are last-key-wins. A duplicate routed key is therefore rejected outright rather than
    /// letting the checked value diverge from the served one.
    /// </summary>
    [Theory]
    [InlineData("""{"model":"first","model":"second"}""")]
    [InlineData("""{"model":"same","model":"same"}""")]
    [InlineData("""{"model":"gpt","stream":false,"stream":true}""")]
    [InlineData("""{"model":"gpt","max_tokens":1,"max_tokens":100000}""")]
    [InlineData("""{"model":"gpt","max_completion_tokens":1,"max_completion_tokens":2}""")]
    // A first occurrence of the wrong kind still counts as seen.
    [InlineData("""{"model":1,"model":"second"}""")]
    [InlineData("""{"model":"gpt","stream":"yes","stream":true}""")]
    public async Task ParseAsync_DuplicateTopLevelRoutedProperties_AreRejected(string json)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var act = () => InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        (await act.Should().ThrowAsync<JsonException>()).WithMessage("*Duplicate top-level*");
    }

    /// <summary>Only the routed keys are policed; other duplicates are the upstream's business.</summary>
    [Fact]
    public async Task ParseAsync_DuplicateUnroutedProperties_AreTolerated()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(
            """{"temperature":1,"model":"gpt","temperature":2,"messages":[{"model":"a"},{"model":"b"}]}"""));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.Model.Should().Be("gpt");
    }

    [Theory]
    [InlineData("""{"model":"alias"}""")]
    [InlineData("""{"model"  :   "alias"  , "stream": true }""")]
    [InlineData("""{"stream":false,"messages":[{"c":"{}[]\"x"}],"model":"alias"}""")]
    [InlineData("""{"model":"ali-as"}""")]
    public async Task ParseAsync_ModelValueRange_DelimitsTheRawTokenExactly(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await using var body = new MemoryStream(bytes);

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        var range = info.ModelValueRange;
        range.Should().NotBeNull();

        var token = Encoding.UTF8.GetString(bytes, (int)range!.Value.Start, (int)range.Value.Length);
        token.Should().StartWith("\"").And.EndWith("\"");
        JsonSerializer.Deserialize<string>(token).Should().Be(info.Model);
    }

    [Fact]
    public async Task ParseAsync_NoModelProperty_ReportsNoRange()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes("""{"stream":true}"""));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.Model.Should().BeNull();
        info.ModelValueRange.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    [InlineData("""{"model":"gpt" """)]
    [InlineData("not json at all")]
    // Trailing content was rejected by the document-based parse this replaced; forwarding a body the
    // gateway called valid but no upstream would accept is worse than rejecting it here.
    [InlineData("""{"model":"gpt"} trailing""")]
    [InlineData("""{"model":"gpt"}{"model":"second"}""")]
    public async Task ParseAsync_NotAJsonObject_Throws(string json)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAnyAsync<JsonException>(
            () => InferenceRequestParser.ParseAsync(body, CancellationToken.None));
    }

    /// <summary>
    /// Callers forward the same buffered body afterwards and take its Length as the outbound
    /// Content-Length, so the parser must leave nothing unread behind the JSON document.
    /// </summary>
    [Fact]
    public async Task ParseAsync_BufferedBody_IsDrainedSoItsLengthIsComplete()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"model":"gpt","stream":false}      """);
        await using var buffered = new FileBufferingReadStream(
            new NonSeekableStream(new MemoryStream(bytes)),
            memoryThreshold: 16);

        var info = await InferenceRequestParser.ParseAsync(buffered, CancellationToken.None);

        info.Model.Should().Be("gpt");
        buffered.Length.Should().Be(bytes.Length, "the whole body must be buffered for forwarding");
    }

    /// <summary>
    /// The real request body is a non-seekable network stream wrapped for rewind, delivered in small
    /// chunks. Property names and their values routinely straddle those chunk boundaries, so the scan
    /// has to carry its state across refills.
    /// </summary>
    [Fact]
    public async Task ParseAsync_BodyDeliveredInTinyChunks_ParsesAcrossBufferRefills()
    {
        var json = $$"""{"messages":[{"role":"user","content":"{{new string('y', 200_000)}}"}],"model":"gpt","stream":true,"max_tokens":64}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        await using var body = new NonSeekableStream(new MemoryStream(bytes), maxReadBytes: 7);

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.Model.Should().Be("gpt");
        info.Stream.Should().BeTrue();
        info.MaxTokens.Should().Be(64);

        var range = info.ModelValueRange;
        range.Should().NotBeNull();
        Encoding.UTF8.GetString(bytes, (int)range!.Value.Start, (int)range.Value.Length)
            .Should().Be("\"gpt\"", "offsets must stay absolute across buffer refills");
    }

    /// <summary>
    /// The defect this covers: JsonDocument.ParseAsync materialised the whole body to reach three
    /// scalars, and against a buffered request stream (whose Length reads 0 until it has been read)
    /// it grew by doubling — ~4.8x the body size in allocations, on a path that ran twice per
    /// request. The scan holds one buffer that only grows to the largest single token.
    /// </summary>
    [Fact]
    public async Task ParseAsync_LargeBodyOfManySmallTokens_DoesNotAllocateProportionally()
    {
        var builder = new StringBuilder("""{"model":"gpt",""");
        builder.Append("\"messages\":[");
        for (var i = 0; i < 40_000; i++)
        {
            builder.Append(i == 0 ? "" : ",").Append("""{"role":"user","content":"a short message"}""");
        }

        builder.Append("],\"stream\":true}");
        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        bytes.Length.Should().BeGreaterThan(1_000_000);

        await using var body = new MemoryStream(bytes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        info.Model.Should().Be("gpt");
        info.Stream.Should().BeTrue();
        allocated.Should().BeLessThan(
            bytes.Length / 4,
            "the scan must not materialise the document to read three top-level scalars");
    }

    private sealed class NonSeekableStream(Stream inner, int maxReadBytes = 64 * 1024) : Stream
    {
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
            inner.Read(buffer, offset, Math.Min(count, maxReadBytes));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, maxReadBytes)], cancellationToken);

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer.AsMemory(offset, Math.Min(count, maxReadBytes)), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
