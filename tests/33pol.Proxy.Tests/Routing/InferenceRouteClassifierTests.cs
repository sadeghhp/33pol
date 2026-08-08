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
    [InlineData("/v1/rerank")]
    public void IsRoutableInference_PostExactInferencePaths_ReturnsTrue(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;

        InferenceRouteClassifier.IsRoutableInference(context).Should().BeTrue();
    }

    [Fact]
    public void IsRoutableInference_PostRerank_ReturnsTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/v1/rerank";

        InferenceRouteClassifier.IsRoutableInference(context).Should().BeTrue();
    }

    /// <summary>
    /// A path that merely <em>ends</em> with an inference path is not routable. Suffix matching let
    /// any prefix reach the router while authorization — which selects its policy by prefix — matched
    /// no policy and skipped evaluation entirely, so an admin-only key could perform inference on
    /// "/api/v1/chat/completions" though it is refused on "/v1/chat/completions".
    /// </summary>
    [Theory]
    [InlineData("/api/v1/chat/completions")]
    [InlineData("/x/v1/chat/completions")]
    [InlineData("/v1/chat/completions/../v1/chat/completions")]
    public void IsRoutableInference_PrefixedInferencePaths_ReturnsFalse(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;

        InferenceRouteClassifier.IsRoutableInference(context).Should().BeFalse();
    }
}
