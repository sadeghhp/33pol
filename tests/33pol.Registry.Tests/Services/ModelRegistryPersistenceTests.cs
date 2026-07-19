using FluentAssertions;
using Pol33.Core.Models;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ModelRegistryPersistenceTests
{
    [Fact]
    public void CloneModel_PreservesPublicAccess()
    {
        var source = new ModelConfig { Id = "a", Url = "http://a", PublicAccess = true };

        var clone = ModelRegistryPersistence.CloneModel(source);

        clone.PublicAccess.Should().BeTrue();
    }

    [Fact]
    public void CloneModel_WithUpstreamAuth_PreservesAuthConfig()
    {
        var source = new ModelConfig
        {
            Id = "or-model",
            Url = "https://openrouter.ai/api",
            MaxContextLength = 128000,
            Aliases = ["alias"],
            UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "OPENROUTER_API_KEY" },
        };

        var clone = ModelRegistryPersistence.CloneModel(source);

        clone.UpstreamAuth.Should().NotBeNull();
        clone.UpstreamAuth!.Type.Should().Be("bearer");
        clone.UpstreamAuth.EnvVar.Should().Be("OPENROUTER_API_KEY");
    }
}
