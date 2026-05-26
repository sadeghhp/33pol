using Microsoft.AspNetCore.Http;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Tests.Routing;

public sealed class InferenceRouteClassifierTests
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/stats")]
    [InlineData("/metrics")]
    [InlineData("/admin/api/config/status")]
    [InlineData("/v1/models")]
    public void IsPassthroughPath_KnownPrefixes_ReturnsTrue(string path)
    {
        InferenceRouteClassifier.IsPassthroughPath(new PathString(path)).Should().BeTrue();
    }

    [Fact]
    public void IsRoutableInference_PostChatCompletions_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/chat/completions";

        InferenceRouteClassifier.IsRoutableInference(context).Should().BeTrue();
    }

    [Fact]
    public void IsRoutableInference_GetChatCompletions_ReturnsFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/v1/chat/completions";

        InferenceRouteClassifier.IsRoutableInference(context).Should().BeFalse();
    }

    [Theory]
    [InlineData("/v1/completions")]
    [InlineData("/v1/embeddings")]
    [InlineData("/api/v1/chat/completions")]
    public void IsRoutableInference_PostInferenceSuffixes_ReturnsTrue(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;

        InferenceRouteClassifier.IsRoutableInference(context).Should().BeTrue();
    }
}
