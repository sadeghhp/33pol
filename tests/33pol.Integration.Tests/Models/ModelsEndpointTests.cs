using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Models;

[Trait("Category", "V1Parity")]
public sealed class ModelsEndpointTests
{
    private readonly HttpClient _client = CreateModelsClient();

    private static HttpClient CreateModelsClient()
    {
        var configPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "models-golden.json"));

        return GatewayWebApplicationFactory.Create(
            configureConfiguration: config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:ModelsConfigPath"] = configPath,
                    ["Gateway:RegistryWatchEnabled"] = "false",
                });
            }).CreateClient();
    }

    [Fact]
    public async Task GetModels_ReturnsGoldenShape()
    {
        var response = await _client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var actual = (await response.Content.ReadAsStringAsync()).Trim();
        var expectedPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..",
            "TestData",
            "v1-models-list.golden.json");
        var expected = File.ReadAllText(Path.GetFullPath(expectedPath)).Trim();

        actual.Should().Be(expected);
    }

    [Fact]
    public async Task GetModels_WhenAllBackendsUnhealthy_ReturnsEmptyData()
    {
        using var factory = GatewayWebApplicationFactory.Create(healthStore: new AlwaysUnhealthyBackendHealthStore());
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("object").GetString().Should().Be("list");
        json.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetModelByAlias_ReturnsCanonicalId()
    {
        var response = await _client.GetAsync("/v1/models/gpt-local");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("id").GetString().Should().Be("local-mock");
        json.RootElement.GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetModel_Unknown_Returns404WithModelNotFound()
    {
        var response = await _client.GetAsync("/v1/models/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("model_not_found");
    }
}
