using Pol33.Core.Configuration;

namespace Pol33.Core.Tests.Configuration;

public sealed class CorsOriginMatcherTests
{
    private static readonly string[] GitHubPagesWildcard = ["https://*.github.io"];

    [Theory]
    [InlineData("https://sadeghhp.github.io")]
    [InlineData("https://foo.github.io")]
    public void IsOriginAllowed_GithubPagesWildcard_MatchesSubdomains(string origin)
    {
        CorsOriginMatcher.IsOriginAllowed(origin, GitHubPagesWildcard).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://github.io")]
    [InlineData("https://evil.github.io.evil.com")]
    [InlineData("http://sadeghhp.github.io")]
    [InlineData("https://sadeghhp.github.io/path")]
    [InlineData("https://evil.example.com")]
    public void IsOriginAllowed_GithubPagesWildcard_RejectsNonMatchingOrigins(string origin)
    {
        CorsOriginMatcher.IsOriginAllowed(origin, GitHubPagesWildcard).Should().BeFalse();
    }

    /// <summary>
    /// A wildcard with a port used to validate but could never match, because the suffix was
    /// compared against Uri.Host, which never carries the port.
    /// </summary>
    [Theory]
    [InlineData("https://app.example.com:8443", true)]
    [InlineData("https://other.example.com:8443", true)]
    [InlineData("https://app.example.com", false)]
    [InlineData("https://app.example.com:9443", false)]
    [InlineData("https://example.com:8443", false)]
    public void IsOriginAllowed_WildcardWithPort_MatchesOnlyThatPort(string origin, bool expected)
    {
        CorsOriginMatcher.IsOriginAllowed(origin, ["https://*.example.com:8443"]).Should().Be(expected);
    }

    [Fact]
    public void IsOriginAllowed_WildcardWithoutPort_StillMatchesAnyPort()
    {
        CorsOriginMatcher.IsOriginAllowed("https://foo.github.io:8443", GitHubPagesWildcard).Should().BeTrue();
    }

    [Fact]
    public void IsOriginAllowed_ExactOrigin_MatchesNormalizedOrigin()
    {
        string[] patterns = ["http://localhost:5173"];

        CorsOriginMatcher.IsOriginAllowed("http://localhost:5173", patterns).Should().BeTrue();
        CorsOriginMatcher.IsOriginAllowed("http://localhost:5173/", patterns).Should().BeFalse();
    }

    [Fact]
    public void IsOriginAllowed_MixedPatterns_MatchesEither()
    {
        string[] patterns = ["https://*.github.io", "http://localhost:3000"];

        CorsOriginMatcher.IsOriginAllowed("https://sadeghhp.github.io", patterns).Should().BeTrue();
        CorsOriginMatcher.IsOriginAllowed("http://localhost:3000", patterns).Should().BeTrue();
        CorsOriginMatcher.IsOriginAllowed("http://localhost:5173", patterns).Should().BeFalse();
    }
}
