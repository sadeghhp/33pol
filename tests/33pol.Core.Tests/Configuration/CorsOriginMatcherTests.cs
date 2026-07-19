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
