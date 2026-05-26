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
}
