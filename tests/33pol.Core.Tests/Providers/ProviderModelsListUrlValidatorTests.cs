using FluentAssertions;
using Pol33.Core.Providers;

namespace Pol33.Core.Tests.Providers;

public sealed class ProviderModelsListUrlValidatorTests
{
    [Fact]
    public void TryValidate_HttpsUrl_ReturnsTrue()
    {
        ProviderModelsListUrlValidator.TryValidate(
            "https://api.together.xyz/v1/models",
            out var uri,
            out var error).Should().BeTrue();

        uri!.Host.Should().Be("api.together.xyz");
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_Loopback_ReturnsFalse()
    {
        ProviderModelsListUrlValidator.TryValidate(
            "http://127.0.0.1:1234/v1/models",
            out _,
            out var error).Should().BeFalse();

        error.Should().Contain("loopback");
    }

    [Theory]
    [InlineData("http://10.0.0.1/v1/models")]
    [InlineData("http://192.168.1.1/v1/models")]
    [InlineData("http://172.16.0.1/v1/models")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public void TryValidate_PrivateOrMetadataIp_ReturnsFalse(string url)
    {
        ProviderModelsListUrlValidator.TryValidate(url, out _, out var error).Should().BeFalse();

        error.Should().Contain("private");
    }

    [Theory]
    [InlineData("http://[::1]/v1/models")]
    [InlineData("http://[::]/v1/models")]
    public void TryValidate_IPv6LoopbackOrUnspecified_ReturnsFalse(string url)
    {
        ProviderModelsListUrlValidator.TryValidate(url, out _, out var error).Should().BeFalse();

        error.Should().NotBeNull();
    }

    [Fact]
    public void TryValidate_LocalhostHost_ReturnsFalse()
    {
        ProviderModelsListUrlValidator.TryValidate(
            "http://localhost/v1/models",
            out _,
            out var error).Should().BeFalse();

        error.Should().Match(e => e!.Contains("loopback", StringComparison.OrdinalIgnoreCase) ||
                                  e.Contains("localhost", StringComparison.OrdinalIgnoreCase));
    }
}
