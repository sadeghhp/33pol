using System.Text;
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
    [InlineData("""{"model":"gpt","max_tokens":256}""", 256)]
    [InlineData("""{"model":"gpt","max_completion_tokens":512}""", 512)]
    public async Task ParseAsync_MaxTokens_IsCaptured(string json, int expected)
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.MaxTokens.Should().Be(expected);
    }

    [Fact]
    public async Task ParseAsync_MaxTokensOmitted_IsNull()
    {
        await using var body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"gpt"}"""));

        var info = await InferenceRequestParser.ParseAsync(body, CancellationToken.None);

        info.MaxTokens.Should().BeNull();
    }
}
