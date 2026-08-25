using System.Text.Json;
using FluentAssertions;
using Pol33.Core.Models;

namespace Pol33.Core.Tests.Models;

public sealed class ModelRouteStatesTests
{
    [Theory]
    [InlineData("serving", ModelRouteStates.Serving)]
    [InlineData("Serving", ModelRouteStates.Serving)]
    [InlineData("  STOPPED  ", ModelRouteStates.Stopped)]
    [InlineData("stopped", ModelRouteStates.Stopped)]
    public void TryNormalize_AcceptsEitherStateInAnyCasing(string input, string expected)
    {
        ModelRouteStates.TryNormalize(input, out var normalized, out var error).Should().BeTrue();
        normalized.Should().Be(expected);
        error.Should().BeNull();
    }

    /// <summary>Blank means "unspecified", which is the state a route has always had by default.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_Blank_IsServing(string? input)
    {
        ModelRouteStates.TryNormalize(input, out var normalized, out var error).Should().BeTrue();
        normalized.Should().Be(ModelRouteStates.Serving);
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("disabled")]
    [InlineData("off")]
    public void TryNormalize_UnknownState_IsRejectedWithTheAcceptedValues(string input)
    {
        ModelRouteStates.TryNormalize(input, out _, out var error).Should().BeFalse();
        error.Should().Contain("serving").And.Contain("stopped");
    }

    /// <summary>
    /// The load path is deliberately more forgiving than the write path: a value this build does not
    /// recognise must not make the route unloadable, because that would drop it out of the registry
    /// and take the model offline for a reason nobody asked for.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-a-newer-build-wrote")]
    public void Normalize_UnusableValue_FallsBackToServing(string? input)
    {
        ModelRouteStates.Normalize(input).Should().Be(ModelRouteStates.Serving);
    }

    /// <summary>
    /// Every models.json written before this field existed has no <c>state</c>, and those routes were
    /// serving. Deserialization must keep them that way.
    /// </summary>
    [Fact]
    public void ModelConfig_DeserializedWithoutState_IsServing()
    {
        var model = JsonSerializer.Deserialize<ModelConfig>(
            """{"id":"legacy","url":"http://legacy","maxContextLength":8192}""")!;

        model.State.Should().Be(ModelRouteStates.Serving);
        model.IsServing().Should().BeTrue();
        model.IsStopped().Should().BeFalse();
    }

    [Fact]
    public void ModelConfig_RoundTripsStateThroughJson()
    {
        var model = JsonSerializer.Deserialize<ModelConfig>(
            """{"id":"paused-route","url":"http://x","state":"stopped"}""")!;

        model.IsStopped().Should().BeTrue();
        JsonSerializer.Serialize(model).Should().Contain("\"state\":\"stopped\"");
    }

    [Fact]
    public void IsStopped_OnANullModel_IsFalse_AndIsServingIsAlsoFalse()
    {
        ModelConfig? model = null;

        model.IsStopped().Should().BeFalse();
        model.IsServing().Should().BeFalse("a route that does not exist serves nothing");
    }
}
