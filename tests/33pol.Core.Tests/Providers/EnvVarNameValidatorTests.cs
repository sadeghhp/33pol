using FluentAssertions;
using Pol33.Core.Providers;

namespace Pol33.Core.Tests.Providers;

public sealed class EnvVarNameValidatorTests
{
    [Theory]
    [InlineData("OPENROUTER_API_KEY")]
    [InlineData("TOGETHER_API_KEY")]
    [InlineData("_PRIVATE")]
    public void TryValidate_ValidName_ReturnsTrue(string name)
    {
        EnvVarNameValidator.TryValidate(name, out var normalized, out var error).Should().BeTrue();
        normalized.Should().Be(name);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_Empty_ReturnsFalse(string? name)
    {
        EnvVarNameValidator.TryValidate(name, out _, out var error).Should().BeFalse();
        error.Should().Contain("required");
    }

    [Fact]
    public void TryValidate_OpenRouterSecret_ReturnsFalse()
    {
        EnvVarNameValidator.TryValidate(
            "sk-or-v1-abcdef0123456789",
            out _,
            out var error).Should().BeFalse();

        error.Should().Contain("not the API key");
    }

    [Fact]
    public void TryValidate_BearerPrefix_ReturnsFalse()
    {
        EnvVarNameValidator.TryValidate("Bearer abc", out _, out var error).Should().BeFalse();
        error.Should().Contain("not the API key");
    }

    [Fact]
    public void TryValidate_InvalidCharacters_ReturnsFalse()
    {
        EnvVarNameValidator.TryValidate("bad-name", out _, out var error).Should().BeFalse();
        error.Should().Contain("valid environment variable name");
    }

    [Fact]
    public void TryValidate_LeadingDigit_ReturnsFalse()
    {
        EnvVarNameValidator.TryValidate("1KEY", out _, out var error).Should().BeFalse();
        error.Should().Contain("valid environment variable name");
    }
}
