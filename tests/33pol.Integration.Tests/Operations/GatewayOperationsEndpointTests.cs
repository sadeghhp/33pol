using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Operations;

public sealed class GatewayOperationsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public GatewayOperationsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    /// <summary>
    /// The model registry is populated by a hosted service, so under load the first request can
    /// arrive before it has finished. Poll rather than asserting on a single shot — the fixed-shot
    /// version failed intermittently whenever the suite ran under parallel load.
    /// </summary>
    [Fact]
    public async Task GetHealth_ReturnsGatewayBackendShape()
    {
        HttpResponseMessage? response = null;
        JsonDocument? json = null;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            json?.Dispose();
            response?.Dispose();

            response = await _client.GetAsync("/health");
            json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            if (json.RootElement.GetProperty("totalBackends").GetInt32() > 0)
            {
                break;
            }

            await Task.Delay(100);
        }

        using (json)
        using (response)
        {
            response!.StatusCode.Should().Be(HttpStatusCode.OK);
            json!.RootElement.GetProperty("status").GetString().Should().Be("healthy");
            json.RootElement.GetProperty("totalBackends").GetInt32().Should().BeGreaterThan(0);
            json.RootElement.GetProperty("backends").GetArrayLength().Should().BeGreaterThan(0);
        }
    }

    /// <summary>
    /// This fixture runs with authentication off, which is the only reason an unauthenticated call
    /// gets the payload. The authorization contract is covered by
    /// <see cref="GetStats_WithAuthenticationEnabled_RequiresAdminKey"/>.
    /// </summary>
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

    /// <summary>
    /// The snapshot names every model that has served traffic and how often each one failed, so it is
    /// gated exactly like the console's own summary. Serving it anonymously let any caller enumerate
    /// the registry and read the traffic profile; probes that only need up/down use /health.
    /// </summary>
    [Fact]
    public async Task GetStats_WithAuthenticationEnabled_RequiresAdminKey()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/stats");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");
        var authorized = await client.GetAsync("/stats");
        authorized.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Liveness and readiness stay anonymous: probes must not need a credential.</summary>
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthProbes_WithAuthenticationEnabled_StayAnonymous(string path)
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
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
