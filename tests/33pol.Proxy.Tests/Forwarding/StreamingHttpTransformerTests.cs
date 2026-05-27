using System.Net;
using Microsoft.AspNetCore.Http;
using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class StreamingHttpTransformerTests
{
    [Fact]
    public async Task TransformRequestAsync_SetsOutboundUriForOpenRouterBase()
    {
        var transformer = new StreamingHttpTransformer(
            isStreaming: false,
            clientModelName: null,
            canonicalModelId: "gpt-4o");
        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/chat/completions";
        var proxyRequest = new HttpRequestMessage(HttpMethod.Post, "http://upstream/v1/chat/completions");

        await transformer.TransformRequestAsync(
            context,
            proxyRequest,
            "https://openrouter.ai/api",
            CancellationToken.None);

        proxyRequest.RequestUri!.AbsoluteUri.Should().Be("https://openrouter.ai/api/v1/chat/completions");
    }

    [Theory]
    [InlineData("{\"model\":\"alias\"}", "canonical")]
    [InlineData("{\"model\": \"alias\"}", "canonical")]
    public void RewriteModelProperty_AliasSpacingVariants_RewritesCanonicalId(string json, string canonical)
    {
        var rewritten = StreamingHttpTransformer.RewriteModelProperty(json, canonical);

        rewritten.Should().Contain($"\"model\":\"{canonical}\"");
        rewritten.Should().NotContain("alias");
    }
}
