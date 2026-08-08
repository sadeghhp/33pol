using FluentAssertions;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class UpstreamSecretFileCipherTests
{
    [Fact]
    public void EncryptDecrypt_RoundTrips()
    {
        var key = UpstreamSecretFileCipher.DeriveKey("pepper");
        var cipher = UpstreamSecretFileCipher.Encrypt("sk-secret", key);

        UpstreamSecretFileCipher.TryDecrypt(cipher, key, out var plain).Should().BeTrue();
        plain.Should().Be("sk-secret");
    }

    [Fact]
    public void TryDecrypt_TooShortPayload_ReturnsFalse()
    {
        var key = UpstreamSecretFileCipher.DeriveKey("pepper");
        var shortPayload = Convert.ToBase64String(new byte[8]);

        UpstreamSecretFileCipher.TryDecrypt(shortPayload, key, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecrypt_WrongKey_ReturnsFalse()
    {
        var cipher = UpstreamSecretFileCipher.Encrypt("sk-secret", UpstreamSecretFileCipher.DeriveKey("a"));

        UpstreamSecretFileCipher.TryDecrypt(cipher, UpstreamSecretFileCipher.DeriveKey("b"), out _)
            .Should().BeFalse();
    }
}
