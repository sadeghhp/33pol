using System.Diagnostics;
using Pol33.Core.Diagnostics;

namespace Pol33.Core.Tests.Diagnostics;

public sealed class GatewayErrorRedactorTests
{
    [Theory]
    [InlineData("Authorization: Bearer sk-abc123def456ghi789")]
    [InlineData("called with bearer eyJhbGciOiJIUzI1NiJ9.payload.signature")]
    [InlineData("{\"api_key\": \"sk-proj-supersecretvalue\"}")]
    [InlineData("api-key=hunter2hunter2 was rejected")]
    [InlineData("token: abcdefghijklmnop")]
    [InlineData("password=correcthorsebattery")]
    public void Scrub_RemovesCredentials(string text)
    {
        var scrubbed = GatewayErrorRedactor.Scrub(text, 500);

        scrubbed.Should().NotBeNull();
        scrubbed.Should().Contain("***");
        foreach (var secret in new[]
                 {
                     "sk-abc123def456ghi789", "eyJhbGciOiJIUzI1NiJ9.payload.signature",
                     "sk-proj-supersecretvalue", "hunter2hunter2", "abcdefghijklmnop",
                     "correcthorsebattery",
                 })
        {
            scrubbed.Should().NotContain(secret);
        }
    }

    [Fact]
    public void Scrub_RemovesUrlUserInfo()
    {
        var scrubbed = GatewayErrorRedactor.Scrub("failed calling https://admin:s3cret@upstream.internal/v1", 500);

        scrubbed.Should().NotContain("s3cret");
        scrubbed.Should().Contain("***");
    }

    [Fact]
    public void Scrub_RemovesSecretQueryParameters()
    {
        var scrubbed = GatewayErrorRedactor.Scrub("GET /v1/models?api_key=leakedvalue&limit=10 failed", 500);

        scrubbed.Should().NotContain("leakedvalue");
        scrubbed.Should().Contain("limit=10");
    }

    [Fact]
    public void Scrub_TruncatesPastTheLimit()
    {
        var scrubbed = GatewayErrorRedactor.Scrub(new string('x', 100), 10);

        scrubbed.Should().Be(new string('x', 10) + "…");
    }

    [Fact]
    public void Scrub_ReturnsNullForBlankInput()
    {
        GatewayErrorRedactor.Scrub(null, 100).Should().BeNull();
        GatewayErrorRedactor.Scrub("   ", 100).Should().BeNull();
    }

    [Fact]
    public void Scrub_CompletesQuicklyOnLargeHostileInput()
    {
        // These patterns run on the request path against text an upstream controls, so a
        // catastrophic backtrack here would be a denial of service rather than a slow log line.
        var hostile = new string('a', 100_000) + "!";

        var stopwatch = Stopwatch.StartNew();
        GatewayErrorRedactor.Scrub(hostile, 2000);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ScrubUrl_KeepsSchemeHostAndPathOnly()
    {
        GatewayErrorRedactor.ScrubUrl("https://user:pass@api.example.com:8443/v1/chat?api_key=secret")
            .Should().Be("https://api.example.com:8443/v1/chat");
    }

    [Fact]
    public void ScrubUrl_DropsTheEntireQueryStringNotJustKnownKeys()
    {
        // An unrecognized parameter name is exactly how a key gets stored, so the whole query goes.
        GatewayErrorRedactor.ScrubUrl("http://upstream:8000/v1?custom_credential=abc123")
            .Should().Be("http://upstream:8000/v1");
    }

    [Fact]
    public void ScrubUrl_OmitsTheDefaultPort()
    {
        GatewayErrorRedactor.ScrubUrl("https://api.example.com/v1").Should().Be("https://api.example.com/v1");
    }

    [Fact]
    public void ScrubUrl_FallsBackToTextRulesWhenUnparseable()
    {
        GatewayErrorRedactor.ScrubUrl("not a url token=supersecretvalue")
            .Should().NotContain("supersecretvalue");
    }

    [Fact]
    public void ScrubUrl_ReturnsNullForBlankInput()
    {
        GatewayErrorRedactor.ScrubUrl(null).Should().BeNull();
    }
}
