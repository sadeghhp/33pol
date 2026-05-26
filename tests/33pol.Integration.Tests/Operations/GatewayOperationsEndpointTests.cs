using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Pol33.Integration.Tests.Operations;

public sealed class GatewayOperationsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public GatewayOperationsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealth_ReturnsGatewayBackendShape()
    {
        var response = await _client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("status").GetString().Should().Be("healthy");
        json.RootElement.GetProperty("totalBackends").GetInt32().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("backends").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetStats_ReturnsMinimalCounters()
    {
        var response = await _client.GetAsync("/stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("totalRequests").GetInt64().Should().Be(0);
        json.RootElement.GetProperty("uptimeSeconds").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        json.RootElement.TryGetProperty("uptime", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetMetrics_ReturnsPrometheusExposition()
    {
        var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("# HELP");
        body.Should().Contain("# TYPE");
    }
}
