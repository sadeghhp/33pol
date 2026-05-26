using Pol33.Proxy.Forwarding;

namespace Pol33.Proxy.Tests.Forwarding;

public sealed class StreamingHttpTransformerTests
{
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
