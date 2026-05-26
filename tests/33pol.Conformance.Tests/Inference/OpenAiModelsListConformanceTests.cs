using System.Net;
using System.Text.Json;
using Pol33.Conformance.Tests.Support;

namespace Pol33.Conformance.Tests.Inference;

public sealed class OpenAiModelsListConformanceTests
{
    [Fact]
    public async Task GetModels_ReturnsOpenAiListEnvelope()
    {
        await using var factory = ConformanceGatewayFactory.Create();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/models");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.GetProperty("object").GetString().Should().Be("list");
        root.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
        root.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);

        var first = root.GetProperty("data")[0];
        first.GetProperty("object").GetString().Should().Be("model");
        first.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
