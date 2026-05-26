using Pol33.Security.Authentication;

namespace Pol33.Security.Tests.Authentication;

public sealed class PublicGatewayPathsTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    [InlineData("/stats")]
    public void IsAnonymous_PublicProbePaths_ReturnsTrue(string path)
    {
        PublicGatewayPaths.IsAnonymous(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("/v1/models")]
    [InlineData("/admin/api/config/status")]
    [InlineData("/v1/chat/completions")]
    public void IsAnonymous_ProtectedPaths_ReturnsFalse(string path)
    {
        PublicGatewayPaths.IsAnonymous(path).Should().BeFalse();
    }
}
