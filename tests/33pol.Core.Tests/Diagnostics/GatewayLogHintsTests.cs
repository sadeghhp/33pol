using System.Net.Sockets;
using Pol33.Core.Diagnostics;

namespace Pol33.Core.Tests.Diagnostics;

public sealed class GatewayLogHintsTests
{
    [Theory]
    [InlineData("http://host:8000/v1", "/v1/chat/completions", "v1")]
    [InlineData("http://host:8000/v1/", "/v1/embeddings", "v1")]
    [InlineData("https://host/api/v1", "/v1/rerank", "v1")]
    public void HasDuplicatedPathPrefix_DetectsRepeatedSegment(string url, string path, string expected)
    {
        GatewayLogHints.HasDuplicatedPathPrefix(url, path, out var duplicated).Should().BeTrue();
        duplicated.Should().Be(expected);
    }

    [Theory]
    [InlineData("http://host:8000", "/v1/chat/completions")]
    [InlineData("http://host:8000/", "/v1/chat/completions")]
    [InlineData("https://openrouter.ai/api", "/v1/chat/completions")]
    [InlineData("not-a-url", "/v1/chat/completions")]
    public void HasDuplicatedPathPrefix_IgnoresHealthyUrls(string url, string path)
    {
        GatewayLogHints.HasDuplicatedPathPrefix(url, path, out _).Should().BeFalse();
    }

    [Fact]
    public void ForUpstreamStatus_404_WithDoubledPrefix_NamesTheCorrectedUrl()
    {
        var hint = GatewayLogHints.ForUpstreamStatus(
            404, "http://host:8000/v1", "/v1/chat/completions", "demo");

        hint.Should().Contain("http://host:8000/v1/v1/chat/completions");
        hint.Should().Contain("'http://host:8000'");
    }

    [Fact]
    public void ForUpstreamStatus_404_WithRootUrl_MentionsRouteAndModelName()
    {
        var hint = GatewayLogHints.ForUpstreamStatus(
            404, "http://host:8000", "/v1/chat/completions", "demo");

        hint.Should().Contain("/v1/chat/completions");
        hint.Should().Contain("'demo'");
        hint.Should().Contain("case-sensitive");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(503)]
    public void ForUpstreamStatus_CoversTheCommonFailures(int status)
    {
        GatewayLogHints.ForUpstreamStatus(status, "http://host:8000", "/v1/chat/completions", "demo")
            .Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>An unrecognized status gets no hint rather than a guess.</summary>
    [Fact]
    public void ForUpstreamStatus_UnknownStatus_ReturnsNull()
    {
        GatewayLogHints.ForUpstreamStatus(418, "http://host:8000", "/v1/chat/completions", "demo")
            .Should().BeNull();
    }

    [Fact]
    public void ForException_ConnectionRefused_MentionsDockerLocalhost()
    {
        var hint = GatewayLogHints.ForException(
            new HttpRequestException("boom", new SocketException((int)SocketError.ConnectionRefused)));

        hint.Should().Contain("host.docker.internal");
    }

    [Fact]
    public void ForException_HostNotFound_MentionsResolution()
    {
        var hint = GatewayLogHints.ForException(
            new HttpRequestException("boom", new SocketException((int)SocketError.HostNotFound)));

        hint.Should().Contain("does not resolve");
    }

    [Fact]
    public void ForException_Timeout_MentionsTheTimeout()
    {
        GatewayLogHints.ForException(new TaskCanceledException())
            .Should().Contain("timeout");
    }

    [Fact]
    public void ForException_Null_ReturnsNull()
    {
        GatewayLogHints.ForException(null).Should().BeNull();
    }
}
