using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Integration.Tests.Models;

public sealed class ModelsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ModelsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
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
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IBackendHealthStore, AlwaysUnhealthyBackendHealthStore>();
            });
        });

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

    private sealed class AlwaysUnhealthyBackendHealthStore : IBackendHealthStore
    {
        public bool IsBackendHealthy(string modelId) => false;

        public BackendHealth? GetHealth(string modelId) => null;

        public IReadOnlyDictionary<string, BackendHealth> GetAllHealth() =>
            new Dictionary<string, BackendHealth>();

        public void SetHealth(BackendHealth health) => throw new NotSupportedException();
    }
}
