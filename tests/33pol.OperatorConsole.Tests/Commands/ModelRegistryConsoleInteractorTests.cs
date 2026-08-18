using Pol33.Core.Models;
using Pol33.OperatorConsole.Commands;

namespace Pol33.OperatorConsole.Tests.Commands;

public sealed class ModelRegistryConsoleInteractorTests
{
    private static ModelConfig Current() =>
        new()
        {
            Id = "gpt-4o",
            Url = "https://old.example.com/v1",
            UpstreamAuth = new UpstreamAuthConfig { Type = "bearer", EnvVar = "OPENAI_API_KEY", SecretRef = "sec-1" },
            MaxContextLength = 128_000,
            Aliases = ["gpt4o", "four-o"],
            PublicAccess = true,
            Capabilities = ["chat", "embeddings"],
            ModelType = "text-generation",
        };

    [Fact]
    public void BuildEditedModel_OnlyUrlChanged_PreservesEveryOtherField()
    {
        var current = Current();

        var edited = ModelRegistryConsoleInteractor.BuildEditedModel(current, "https://new.example.com/v1", string.Empty);

        edited.Should().NotBeSameAs(current);
        edited.Id.Should().Be("gpt-4o");
        edited.Url.Should().Be("https://new.example.com/v1");
        edited.UpstreamAuth.Should().NotBeNull();
        edited.UpstreamAuth!.EnvVar.Should().Be("OPENAI_API_KEY");
        edited.UpstreamAuth.SecretRef.Should().Be("sec-1");
        edited.MaxContextLength.Should().Be(128_000);
        edited.Aliases.Should().Equal("gpt4o", "four-o");
        edited.PublicAccess.Should().BeTrue();
        edited.Capabilities.Should().Equal("chat", "embeddings");
        edited.ModelType.Should().Be("text-generation");
        current.Url.Should().Be("https://old.example.com/v1", "the registry's copy must not be mutated in place");
    }

    [Fact]
    public void BuildEditedModel_WithAliases_ReplacesAliasesOnly()
    {
        var edited = ModelRegistryConsoleInteractor.BuildEditedModel(Current(), "https://new.example.com/v1", " a1 , a2,,");

        edited.Aliases.Should().Equal("a1", "a2");
        edited.UpstreamAuth.Should().NotBeNull();
        edited.Capabilities.Should().Equal("chat", "embeddings");
    }

    [Fact]
    public void BuildEditedModel_WhitespaceAliases_KeepsExistingAliases()
    {
        var edited = ModelRegistryConsoleInteractor.BuildEditedModel(Current(), "u", "   ");

        edited.Aliases.Should().Equal("gpt4o", "four-o");
    }

    [Fact]
    public void FindModel_MatchesIdCaseInsensitivelyAndByAlias()
    {
        var models = new List<ModelConfig> { Current(), new() { Id = "other", Url = "u" } };

        ModelRegistryConsoleInteractor.FindModel(models, " GPT-4O ").Should().BeSameAs(models[0]);
        ModelRegistryConsoleInteractor.FindModel(models, "four-o").Should().BeSameAs(models[0]);
        ModelRegistryConsoleInteractor.FindModel(models, "missing").Should().BeNull();
    }
}
