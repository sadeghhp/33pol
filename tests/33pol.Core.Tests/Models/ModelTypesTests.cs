using FluentAssertions;
using Pol33.Core.Models;

namespace Pol33.Core.Tests.Models;

public sealed class ModelTypesTests
{
    [Theory]
    [InlineData("embedding", ModelTypes.Embedding)]
    [InlineData("embeddings", ModelTypes.Embedding)]
    [InlineData("  Embeddings  ", ModelTypes.Embedding)]
    [InlineData("text_generation", ModelTypes.TextGeneration)]
    [InlineData("chat", ModelTypes.TextGeneration)]
    [InlineData("RERANK", ModelTypes.Rerank)]
    [InlineData("ocr", ModelTypes.Ocr)]
    [InlineData("text-to-video", ModelTypes.VideoGeneration)]
    public void TryNormalize_FoldsAliasesOntoCanonicalType(string input, string expected)
    {
        ModelTypes.TryNormalize(input, out var normalized, out var error).Should().BeTrue();
        normalized.Should().Be(expected);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_BlankIsUnsetNotInvalid(string? input)
    {
        ModelTypes.TryNormalize(input, out var normalized, out var error).Should().BeTrue();
        normalized.Should().BeNull();
        error.Should().BeNull();
    }

    [Fact]
    public void TryNormalize_UnknownType_ReturnsFalseWithGuidance()
    {
        ModelTypes.TryNormalize("teleportation", out var normalized, out var error).Should().BeFalse();
        normalized.Should().BeNull();
        error.Should().Contain("teleportation").And.Contain(ModelTypes.Embedding);
    }

    [Fact]
    public void Resolve_ExplicitTypeWins()
    {
        var model = new ModelConfig
        {
            Id = "m",
            Url = "http://x",
            ModelType = "embeddings",
            Capabilities = ["chat"],
        };

        ModelTypes.Resolve(model).Should().Be(ModelTypes.Embedding);
    }

    [Fact]
    public void Resolve_NoExplicitType_InfersFromSinglePurposeCapabilities()
    {
        var model = new ModelConfig { Id = "m", Url = "http://x", Capabilities = ["embeddings"] };

        ModelTypes.Resolve(model).Should().Be(ModelTypes.Embedding);
    }

    /// <summary>A model that also serves chat is a text-generation model with an extra route.</summary>
    [Fact]
    public void Resolve_MixedCapabilities_FallsBackToTextGeneration()
    {
        var model = new ModelConfig { Id = "m", Url = "http://x", Capabilities = ["chat", "rerank"] };

        ModelTypes.Resolve(model).Should().Be(ModelTypes.TextGeneration);
    }

    [Fact]
    public void Resolve_NoTypeAndNoCapabilities_DefaultsToTextGeneration()
    {
        ModelTypes.Resolve(new ModelConfig { Id = "m", Url = "http://x" })
            .Should().Be(ModelTypes.TextGeneration);
    }

    [Fact]
    public void Resolve_UnrecognisedExplicitType_FallsBackRatherThanThrowing()
    {
        var model = new ModelConfig { Id = "m", Url = "http://x", ModelType = "teleportation" };

        ModelTypes.Resolve(model).Should().Be(ModelTypes.TextGeneration);
    }

    [Fact]
    public void All_ContainsEveryCanonicalConstant()
    {
        ModelTypes.All.Should().Contain(
        [
            ModelTypes.TextGeneration,
            ModelTypes.Embedding,
            ModelTypes.Rerank,
            ModelTypes.Ocr,
            ModelTypes.ImageGeneration,
            ModelTypes.VideoGeneration,
            ModelTypes.AudioTranscription,
        ]);

        ModelTypes.All.Should().OnlyHaveUniqueItems();
        ModelTypes.All.Should().OnlyContain(t => ModelTypes.IsKnown(t));
    }
}
