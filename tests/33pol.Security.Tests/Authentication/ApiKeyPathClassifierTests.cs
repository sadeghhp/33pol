using Microsoft.AspNetCore.Http;
using Pol33.Security.Authentication;

namespace Pol33.Security.Tests.Authentication;

public sealed class ApiKeyPathClassifierTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/metrics")]
    [InlineData("/stats")]
    public void IsPublicPath_KnownOpsRoutes_ReturnsTrue(string path)
    {
        ApiKeyPathClassifier.IsPublicPath(new PathString(path)).Should().BeTrue();
    }

    [Fact]
    public void IsAdminPath_AdminApiPrefix_ReturnsTrue()
    {
        ApiKeyPathClassifier.IsAdminPath(new PathString("/admin/api/config/status")).Should().BeTrue();
    }

    [Fact]
    public void RequiresInferenceKey_V1Models_ReturnsTrue()
    {
        ApiKeyPathClassifier.RequiresInferenceKey(new PathString("/v1/models")).Should().BeTrue();
    }
}
