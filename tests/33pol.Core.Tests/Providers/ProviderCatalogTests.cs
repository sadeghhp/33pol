using FluentAssertions;
using Pol33.Core.Providers;

namespace Pol33.Core.Tests.Providers;

public sealed class ProviderCatalogTests
{
    [Theory]
    [InlineData("openrouter", "https://openrouter.ai/api")]
    [InlineData("together", "https://api.together.xyz")]
    [InlineData("groq", "https://api.groq.com/openai")]
    [InlineData("dashscope", "https://dashscope-intl.aliyuncs.com/compatible-mode")]
    public void TryGetBuiltIn_KnownProvider_ReturnsDefinition(string id, string expectedBase)
    {
        ProviderCatalog.TryGetBuiltIn(id, out var definition).Should().BeTrue();
        definition!.UpstreamBaseUrl.Should().Be(expectedBase);
    }

    /// <summary>
    /// A local provider's endpoint always resolves to a private address, which the SSRF-guarded
    /// discovery client refuses; advertising a discovery URL for it only produced a misleading
    /// "blocked address" error. It must be marked as not supporting discovery instead.
    /// </summary>
    [Fact]
    public void LocalProvider_DoesNotAdvertiseDiscovery()
    {
        ProviderCatalog.TryGetBuiltIn("lmstudio", out var definition).Should().BeTrue();

        definition!.SupportsDiscovery.Should().BeFalse();
        definition.ModelsListUrl.Should().BeEmpty();
        definition.UpstreamBaseUrl.Should().Be("http://host.docker.internal:1234");
        definition.RequiresUpstreamAuth.Should().BeFalse();
    }

    [Fact]
    public void HostedProviders_AdvertiseAnAbsoluteHttpsDiscoveryUrl()
    {
        foreach (var provider in ProviderCatalog.ListBuiltIn().Where(p => p.SupportsDiscovery))
        {
            Uri.TryCreate(provider.ModelsListUrl, UriKind.Absolute, out var uri).Should().BeTrue(provider.Id);
            uri!.Scheme.Should().Be(Uri.UriSchemeHttps, provider.Id);
        }
    }

    [Fact]
    public void TryGetBuiltIn_Unknown_ReturnsFalse()
    {
        ProviderCatalog.TryGetBuiltIn("not-a-provider", out var definition).Should().BeFalse();
        definition.Should().BeNull();
    }
}
