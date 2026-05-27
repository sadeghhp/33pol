using Microsoft.AspNetCore.Http;
using Pol33.Proxy.Routing;

namespace Pol33.Proxy.Tests.Routing;

public sealed class InferenceDestinationBuilderTests
{
    [Fact]
    public void BuildOutboundUri_OpenRouterApiBase_AppendsUnderApiPath()
    {
        var uri = InferenceDestinationBuilder.BuildOutboundUri(
            "https://openrouter.ai/api",
            new PathString("/v1/chat/completions"),
            QueryString.Empty);

        uri.AbsoluteUri.Should().Be("https://openrouter.ai/api/v1/chat/completions");
    }

    [Theory]
    [InlineData("https://openrouter.ai/api")]
    [InlineData("https://openrouter.ai/api/")]
    public void ToForwarderDestination_EnsuresTrailingSlash(string modelUrl)
    {
        InferenceDestinationBuilder.ToForwarderDestination(modelUrl)
            .Should().Be("https://openrouter.ai/api/");
    }
}
