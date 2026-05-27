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
}
