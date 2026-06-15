using System.Text.Json;
using Pol33.Core.Models;

namespace Pol33.Core.Tests.Models;

public sealed class ModelConfigTests
{
    [Fact]
    public void Deserialize_OmitsPublicAccess_DefaultsFalse()
    {
        const string json = """{"id":"m1","url":"http://localhost:1"}""";

        var model = JsonSerializer.Deserialize<ModelConfig>(json);

        model.Should().NotBeNull();
        model!.PublicAccess.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_PublicAccessTrue_RoundTrips()
    {
        const string json = """{"id":"m1","url":"http://localhost:1","publicAccess":true}""";

        var model = JsonSerializer.Deserialize<ModelConfig>(json);

        model!.PublicAccess.Should().BeTrue();
        JsonSerializer.Serialize(model).Should().Contain("\"publicAccess\":true");
    }

    [Fact]
    public void Deserialize_Capabilities_RoundTrips()
    {
        const string json = """{"id":"m1","url":"http://localhost:1","capabilities":["rerank"]}""";

        var model = JsonSerializer.Deserialize<ModelConfig>(json);

        model.Should().NotBeNull();
        model!.Capabilities.Should().ContainSingle().Which.Should().Be("rerank");
        JsonSerializer.Serialize(model).Should().Contain("\"capabilities\":[\"rerank\"]");
    }

    [Fact]
    public void AllowsPublicGatewayAccess_WhenPublicAccessTrue_ReturnsTrue()
    {
        new ModelConfig { Id = "m1", Url = "http://x", PublicAccess = true }
            .AllowsPublicGatewayAccess()
            .Should()
            .BeTrue();
    }

    [Fact]
    public void AllowsPublicGatewayAccess_WhenNullOrNotPublic_ReturnsFalse()
    {
        ((ModelConfig?)null).AllowsPublicGatewayAccess().Should().BeFalse();
        new ModelConfig { Id = "m1", Url = "http://x" }.AllowsPublicGatewayAccess().Should().BeFalse();
    }

    [Fact]
    public void HasCapability_EmptyList_ReturnsTrue()
    {
        var model = new ModelConfig { Id = "m1", Url = "http://x" };
        model.HasCapability("chat").Should().BeTrue();
        model.HasCapability("rerank").Should().BeTrue();
    }

    [Fact]
    public void HasCapability_ContainsCapability_ReturnsTrue()
    {
        var model = new ModelConfig
        {
            Id = "m1",
            Url = "http://x",
            Capabilities = ["rerank"],
        };

        model.HasCapability("rerank").Should().BeTrue();
    }

    [Fact]
    public void HasCapability_MissingCapability_ReturnsFalse()
    {
        var model = new ModelConfig
        {
            Id = "m1",
            Url = "http://x",
            Capabilities = ["rerank"],
        };

        model.HasCapability("chat").Should().BeFalse();
    }
}
