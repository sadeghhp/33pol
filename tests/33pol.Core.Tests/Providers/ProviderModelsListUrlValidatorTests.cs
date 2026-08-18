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

    /// <summary>
    /// IPv6 transition prefixes embed an IPv4 address; an attacker-controlled name resolving to
    /// e.g. NAT64 64:ff9b::a9fe:a9fe reaches 169.254.169.254 on an IPv6-only cluster.
    /// </summary>
    [Theory]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/latest/meta-data/")] // NAT64 -> 169.254.169.254
    [InlineData("http://[64:ff9b::0a00:0001]/v1/models")] // NAT64 -> 10.0.0.1
    [InlineData("http://[::a9fe:a9fe]/v1/models")] // IPv4-compatible -> 169.254.169.254
    [InlineData("http://[::c0a8:0101]/v1/models")] // IPv4-compatible -> 192.168.1.1
    [InlineData("http://[2002:a9fe:a9fe::1]/v1/models")] // 6to4 -> 169.254.169.254
    [InlineData("http://[2002:0a00:0001::1]/v1/models")] // 6to4 -> 10.0.0.1
    [InlineData("http://[2001:0:a9fe:a9fe::1]/v1/models")] // Teredo server -> 169.254.169.254
    [InlineData("http://[2001:0:0102:0304:0:0:5601:56fe]/v1/models")] // Teredo client ~ -> 169.254.169.1
    [InlineData("http://[fec0::1]/v1/models")] // site-local
    [InlineData("http://[ff02::1]/v1/models")] // multicast
    [InlineData("http://[fe80::1]/v1/models")] // link-local
    [InlineData("http://[fd00::1]/v1/models")] // unique-local
    [InlineData("http://[::ffff:169.254.169.254]/v1/models")] // IPv4-mapped
    public void TryValidate_IPv6TransitionAndScopedAddresses_ReturnsFalse(string url)
    {
        ProviderModelsListUrlValidator.TryValidate(url, out _, out var error).Should().BeFalse();

        error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("http://[2606:4700:4700::1111]/v1/models")] // public IPv6
    [InlineData("http://[64:ff9b::0808:0808]/v1/models")] // NAT64 -> 8.8.8.8 (public)
    [InlineData("http://[2002:0808:0808::1]/v1/models")] // 6to4 -> 8.8.8.8 (public)
    public void TryValidate_PublicIPv6_ReturnsTrue(string url)
    {
        ProviderModelsListUrlValidator.TryValidate(url, out _, out var error).Should().BeTrue(error);
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
