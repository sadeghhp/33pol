using System.Net;
using System.Net.Http.Headers;
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
    /// <para>
    /// The status is deliberately not pinned to <c>healthy</c>. This fixture's upstreams are not
    /// listening, so the moment the first health sweep lands the gateway correctly reports
    /// <c>degraded</c>/503 — and whether the request beats that sweep is a race, which is exactly
    /// how this test flaked. The shape is the contract under test and is identical either way.
    /// </para>
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
            response!.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
            json!.RootElement.GetProperty("status").GetString().Should().BeOneOf("healthy", "degraded");
            json.RootElement.GetProperty("totalBackends").GetInt32().Should().BeGreaterThan(0);
            json.RootElement.GetProperty("backends").GetArrayLength().Should().BeGreaterThan(0);

            // The two must agree: 503 is only correct when nothing is healthy.
            var healthy = json.RootElement.GetProperty("status").GetString() == "healthy";
            response.StatusCode.Should().Be(healthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
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

    /// <summary>
    /// The anonymous shape names each backend's up/down state but never its upstream URL or the
    /// prober's error text — those describe the internal topology and are for operators only.
    /// </summary>
    [Fact]
    public async Task GetHealth_WithAuthenticationEnabled_AnonymousGetsSummaryWithoutUrlOrError()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        using var json = await GetHealthWhenLoadedAsync(client);

        json.RootElement.TryGetProperty("status", out _).Should().BeTrue();
        json.RootElement.GetProperty("totalBackends").GetInt32().Should().BeGreaterThan(0);
        var backends = json.RootElement.GetProperty("backends");
        backends.GetArrayLength().Should().BeGreaterThan(0);
        foreach (var backend in backends.EnumerateArray())
        {
            backend.TryGetProperty("modelId", out _).Should().BeTrue();
            backend.TryGetProperty("isHealthy", out _).Should().BeTrue();
            backend.TryGetProperty("url", out _).Should().BeFalse("the upstream URL is operator-only");
            backend.TryGetProperty("error", out _).Should().BeFalse("probe error text is operator-only");
        }
    }

    [Fact]
    public async Task GetHealth_WithAuthenticationEnabled_OperatorKeyGetsBackendDetail()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        using var json = await GetHealthWhenLoadedAsync(client);

        var backends = json.RootElement.GetProperty("backends");
        backends.GetArrayLength().Should().BeGreaterThan(0);
        foreach (var backend in backends.EnumerateArray())
        {
            backend.GetProperty("url").GetString().Should().NotBeNullOrEmpty();
        }
    }

    /// <summary>An unrecognised key on /health is treated as no key: still 200, still the summary.</summary>
    [Fact]
    public async Task GetHealth_WithAuthenticationEnabled_UnknownKeyStillGetsSummary()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-not-a-real-key");

        using var json = await GetHealthWhenLoadedAsync(client);

        foreach (var backend in json.RootElement.GetProperty("backends").EnumerateArray())
        {
            backend.TryGetProperty("url", out _).Should().BeFalse();
        }
    }

    /// <summary>
    /// This fixture runs with authentication off, so the scrape is served like everything else. The
    /// gated contract is covered by the metrics tests below.
    /// </summary>
    [Fact]
    public async Task GetMetrics_ReturnsPrometheusExposition()
    {
        var response = await _client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("# HELP");
        body.Should().Contain("# TYPE");
    }

    /// <summary>
    /// The exposition carries per-model series, so with keys issued and no scrape token configured
    /// only an Operator key gets it; anonymous and non-operator scrapes are 401.
    /// </summary>
    [Fact]
    public async Task GetMetrics_WithAuthenticationEnabled_RequiresOperatorKeyOrToken()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/metrics");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.Headers.Should().ContainKey("X-33pol-Error-Code");

        using (var withBogusBearer = new HttpRequestMessage(HttpMethod.Get, "/metrics"))
        {
            withBogusBearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
            (await client.SendAsync(withBogusBearer)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");
        var asOperator = await client.GetAsync("/metrics");
        asOperator.StatusCode.Should().Be(HttpStatusCode.OK);
        (await asOperator.Content.ReadAsStringAsync()).Should().Contain("# TYPE");
    }

    [Fact]
    public async Task GetMetrics_WithScrapeToken_AcceptsBearerToken()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            configureSettings: settings => settings["Gateway:Metrics:ScrapeToken"] = "scrape-me-please");
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        using (var wrong = new HttpRequestMessage(HttpMethod.Get, "/metrics"))
        {
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scrape-me-pleasE");
            (await client.SendAsync(wrong)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // The token is a bearer credential, not an API key: X-API-Key must not accept it.
        using (var viaApiKeyHeader = new HttpRequestMessage(HttpMethod.Get, "/metrics"))
        {
            viaApiKeyHeader.Headers.Add("X-API-Key", "scrape-me-please");
            (await client.SendAsync(viaApiKeyHeader)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var right = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "scrape-me-please");
        var response = await client.SendAsync(right);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("# TYPE");
    }

    [Fact]
    public async Task GetMetrics_WithAllowAnonymous_ServesWithoutCredential()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            configureSettings: settings => settings["Gateway:Metrics:AllowAnonymous"] = "true");
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>The registry loads asynchronously; poll until at least one backend is reported.</summary>
    private static async Task<JsonDocument> GetHealthWhenLoadedAsync(HttpClient client)
    {
        JsonDocument? json = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            json?.Dispose();
            using var response = await client.GetAsync("/health");
            response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
            json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.GetProperty("totalBackends").GetInt32() > 0)
            {
                return json;
            }

            await Task.Delay(100);
        }

        return json!;
    }
}
