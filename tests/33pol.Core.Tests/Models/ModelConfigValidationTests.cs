using FluentAssertions;
using Pol33.Core.Models;

namespace Pol33.Core.Tests.Models;

public sealed class ModelConfigValidationTests
{
    [Fact]
    public void TryValidate_NoUpstreamAuth_ReturnsTrue()
    {
        ModelConfigValidation.TryValidate(
            new ModelConfig { Id = "m", Url = "http://x", Aliases = [] },
            out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_SecretEnvVar_ReturnsFalse()
    {
        ModelConfigValidation.TryValidate(
            new ModelConfig
            {
                Id = "m",
                Url = "http://x",
                Aliases = [],
                UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "sk-or-v1-abcdef" }
            },
            out var error).Should().BeFalse();

        error.Should().Contain("not the API key");
    }
}
