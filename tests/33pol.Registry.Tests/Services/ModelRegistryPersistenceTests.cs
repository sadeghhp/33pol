using System.Text.Json;
using FluentAssertions;
using Pol33.Core.Models;
using Pol33.Registry.Services;

namespace Pol33.Registry.Tests.Services;

public sealed class ModelRegistryPersistenceTests
{
    [Fact]
    public void BuildLookup_EmptyList_ThrowsInvalidOperationException()
    {
        var act = () => ModelRegistryPersistence.BuildLookup([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty model list*");
    }

    [Fact]
    public void BuildLookup_MissingUrl_ThrowsJsonException()
    {
        var act = () => ModelRegistryPersistence.BuildLookup(
        [
            new ModelConfig { Id = "missing-url", Url = "", Aliases = [] },
        ]);

        act.Should().Throw<JsonException>()
            .WithMessage("*missing required 'url'*");
    }

    [Fact]
    public void BuildLookup_DuplicateModelId_ThrowsJsonException()
    {
        var act = () => ModelRegistryPersistence.BuildLookup(
        [
            new ModelConfig { Id = "dup", Url = "http://a", Aliases = [] },
            new ModelConfig { Id = "dup", Url = "http://b", Aliases = [] },
        ]);

        act.Should().Throw<JsonException>()
            .WithMessage("*Duplicate model id*");
    }

    [Fact]
    public void BuildLookup_DuplicateAliasAcrossModels_ThrowsJsonException()
    {
        var act = () => ModelRegistryPersistence.BuildLookup(
        [
            new ModelConfig { Id = "a", Url = "http://a", Aliases = ["shared"] },
            new ModelConfig { Id = "b", Url = "http://b", Aliases = ["shared"] },
        ]);

        act.Should().Throw<JsonException>()
            .WithMessage("*Duplicate alias*");
    }

    [Fact]
    public void Deserialize_ValidJson_ReturnsModels()
    {
        var config = ModelRegistryPersistence.Deserialize("""
            {
              "models": [
                { "id": "one", "url": "http://one", "aliases": ["alias-one"] }
              ]
            }
            """);

        config.Models.Should().HaveCount(1);
        config.Models[0].Id.Should().Be("one");
    }
}
