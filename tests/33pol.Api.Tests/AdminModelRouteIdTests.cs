using Pol33.Api;

namespace Pol33.Api.Tests;

public sealed class AdminModelRouteIdTests
{
    [Theory]
    [InlineData("simple-id", "simple-id")]
    [InlineData("z-ai%2Fglm-4.5-air:free", "z-ai/glm-4.5-air:free")]
    [InlineData("openai%2Fgpt-oss-120b%3Afree", "openai/gpt-oss-120b:free")]
    public void Decode_EncodedRouteId_ReturnsCanonicalId(string routeId, string expected) =>
        AdminModelRouteId.Decode(routeId).Should().Be(expected);

    [Fact]
    public void Decode_PlainSlashId_ReturnsUnchanged() =>
        AdminModelRouteId.Decode("z-ai/glm-4.5-air:free").Should().Be("z-ai/glm-4.5-air:free");

    [Fact]
    public void Decode_Empty_ReturnsEmpty() =>
        AdminModelRouteId.Decode("").Should().Be("");
}
