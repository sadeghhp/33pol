using Pol33.Core.Security;

namespace Pol33.Core.Tests.Security;

/// <summary>
/// One definition of "does this request carry a credential", shared by the security layer that
/// authenticates it and the proxy layer that decides whether a limiter applies. Two implementations
/// would eventually disagree, and the symptom would be a limiter charging the wrong requests.
/// </summary>
public sealed class GatewayCredentialTests
{
    [Fact]
    public void Extract_PrefersTheApiKeyHeader()
    {
        GatewayCredential.Extract("sk-header", "Bearer sk-bearer").Should().Be("sk-header");
    }

    /// <summary>
    /// Some proxies and SDKs always send X-API-Key, empty when they have nothing to put in it. A
    /// blank one must not shadow a real bearer token on the same request.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Extract_BlankApiKeyHeader_FallsBackToTheBearerToken(string header)
    {
        GatewayCredential.Extract(header, "Bearer sk-bearer").Should().Be("sk-bearer");
    }

    [Fact]
    public void Extract_BearerScheme_IsCaseInsensitiveAndTrimmed()
    {
        GatewayCredential.Extract(null, "bearer   sk-bearer  ").Should().Be("sk-bearer");
    }

    /// <summary>A bearer prefix with nothing after it is no credential, not an empty one.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "Bearer ")]
    [InlineData(null, "Bearer    ")]
    [InlineData(null, "Basic dXNlcjpwYXNz")]
    public void Extract_WithoutAUsableCredential_IsNull(string? apiKey, string? authorization)
    {
        GatewayCredential.Extract(apiKey, authorization).Should().BeNull();
        GatewayCredential.IsPresent(apiKey, authorization).Should().BeFalse();
    }

    [Fact]
    public void IsPresent_MatchesExtract()
    {
        GatewayCredential.IsPresent("sk-header", null).Should().BeTrue();
        GatewayCredential.IsPresent(null, "Bearer sk-bearer").Should().BeTrue();
    }
}
