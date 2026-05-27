using FluentAssertions;
using Pol33.Core.Providers;

namespace Pol33.Core.Tests.Providers;

public sealed class ProviderCatalogTests
{
    [Theory]
    [InlineData("openrouter", "https://openrouter.ai/api")]
    [InlineData("together", "https://api.together.xyz")]
    [InlineData("groq", "https://api.groq.com/openai")]
    public void TryGetBuiltIn_KnownProvider_ReturnsDefinition(string id, string expectedBase)
    {
        ProviderCatalog.TryGetBuiltIn(id, out var definition).Should().BeTrue();
        definition!.UpstreamBaseUrl.Should().Be(expectedBase);
    }

    [Fact]
    public void TryGetBuiltIn_Unknown_ReturnsFalse()
    {
        ProviderCatalog.TryGetBuiltIn("not-a-provider", out var definition).Should().BeFalse();
        definition.Should().BeNull();
    }
}
