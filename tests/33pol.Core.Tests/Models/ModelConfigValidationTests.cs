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

    /// <summary>
    /// url used to be checked only for blankness by callers, so "not a url" or an ftp:// URL was
    /// persisted and failed later in the forwarder and threw in the health checker.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://files.example.com")]
    [InlineData("/relative/path")]
    [InlineData("localhost:8080")]
    public void TryValidate_InvalidUrl_ReturnsFalse(string url)
    {
        ModelConfigValidation.TryValidate(new ModelConfig { Id = "m", Url = url, Aliases = [] }, out var error)
            .Should().BeFalse();

        error.Should().Contain("url");
    }

    [Theory]
    [InlineData("http://host.docker.internal:1234")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData(" http://x ")]
    public void TryValidate_HttpOrHttpsUrl_ReturnsTrue(string url)
    {
        ModelConfigValidation.TryValidate(new ModelConfig { Id = "m", Url = url, Aliases = [] }, out var error)
            .Should().BeTrue(error);
    }

    /// <summary>An update body may leave the id blank to keep the existing one; the validator must not reject that.</summary>
    [Fact]
    public void TryValidate_BlankId_IsNotRejectedHere()
    {
        ModelConfigValidation.TryValidate(new ModelConfig { Id = "", Url = "http://x", Aliases = [] }, out var error)
            .Should().BeTrue(error);
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

    [Fact]
    public void TryValidate_SecretRef_Valid_ReturnsTrue()
    {
        ModelConfigValidation.TryValidate(
            new ModelConfig
            {
                Id = "my-model",
                Url = "http://x",
                Aliases = [],
                UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    SecretRef = "file:model:my-model"
                }
            },
            out var error).Should().BeTrue();

        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_BothEnvAndSecretRef_ReturnsFalse()
    {
        ModelConfigValidation.TryValidate(
            new ModelConfig
            {
                Id = "m",
                Url = "http://x",
                Aliases = [],
                UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    EnvVar = "OPENROUTER_API_KEY",
                    SecretRef = "file:model:m"
                }
            },
            out var error).Should().BeFalse();

        error.Should().Contain("both");
    }

    [Fact]
    public void TryValidate_UnknownModelType_ReturnsFalse()
    {
        ModelConfigValidation.TryValidate(
            new ModelConfig { Id = "m", Url = "http://x", ModelType = "teleportation" },
            out var error).Should().BeFalse();
        error.Should().Contain("modelType");
    }

    [Fact]
    public void TryValidate_KnownModelTypeAlias_ReturnsTrue()
    {
        ModelConfigValidation.TryValidate(
            new ModelConfig { Id = "m", Url = "http://x", ModelType = "embeddings" },
            out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_UnknownState_IsRejected()
    {
        var model = new ModelConfig { Id = "m", Url = "http://upstream", State = "paused" };

        ModelConfigValidation.TryValidate(model, out var error).Should().BeFalse();
        error.Should().Contain("state");
    }

    [Theory]
    [InlineData(ModelRouteStates.Serving)]
    [InlineData(ModelRouteStates.Stopped)]
    public void TryValidate_EitherKnownState_IsAccepted(string state)
    {
        var model = new ModelConfig { Id = "m", Url = "http://upstream", State = state };

        ModelConfigValidation.TryValidate(model, out var error).Should().BeTrue(error);
    }
}
