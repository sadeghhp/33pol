using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Support;
using Pol33.Registry.Health;

namespace Pol33.Integration.Tests.Health;

public sealed class HealthReadyEndpointTests
{
    [Fact]
    public async Task GetHealthReady_WhenHealthy_ReturnsOk()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"ready\"");
    }

    [Fact]
    public async Task GetHealthReady_WhenDraining_Returns503()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IGatewayDrainState>().BeginDrain();

        var client = factory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"isDraining\":true");
    }

    [Fact]
    public async Task GetHealthReady_AllBackendsUnhealthy_Returns503()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            healthStore: new AlwaysUnhealthyBackendHealthStore());
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// The GW-02a regression, against the real store rather than a stub. Every integration factory
    /// substitutes an always-healthy store and removes the health sweep, which is exactly why this
    /// went unnoticed: readiness had never been exercised against the store production uses. With
    /// routes configured and no sweep having run, readiness used to be 200 — <c>IsBackendHealthy</c>
    /// answers <c>!HealthCheckStrictMode</c>, true by default, for a model it has never seen — so a
    /// pod passed its gate and took traffic before any upstream had been reached.
    /// </summary>
    [Fact]
    public async Task GetHealthReady_RealStore_BeforeAnyProbe_Returns503()
    {
        var store = new BackendHealthStore(Options.Create(new GatewayOptions()));
        await using var factory = GatewayWebApplicationFactory.Create(healthStore: store);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configuredBackends").GetInt32().Should().BeGreaterThan(0);
        body.GetProperty("probedBackends").GetInt32().Should().Be(0);
        body.GetProperty("healthyBackends").GetInt32().Should().Be(0);
    }

    /// <summary>And it flips as soon as one real probe verdict lands.</summary>
    [Fact]
    public async Task GetHealthReady_RealStore_AfterAHealthyProbe_Returns200()
    {
        var store = new BackendHealthStore(Options.Create(new GatewayOptions()));
        await using var factory = GatewayWebApplicationFactory.Create(healthStore: store);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IModelRegistry>();
        var first = registry.GetAllModels()[0];
        store.SetHealth(new BackendHealth(first.Id, first.Url, true, 200, null, DateTimeOffset.UtcNow));

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("probedBackends").GetInt32().Should().Be(1);
        body.GetProperty("healthyBackends").GetInt32().Should().Be(1);
    }

    /// <summary>
    /// An unreachable backend that has actually been probed is an outage, and readiness says so
    /// rather than waiting out the failure threshold the router uses.
    /// </summary>
    [Fact]
    public async Task GetHealthReady_RealStore_AfterAnUnhealthyProbe_Returns503()
    {
        var store = new BackendHealthStore(Options.Create(new GatewayOptions()));
        await using var factory = GatewayWebApplicationFactory.Create(healthStore: store);
        var client = factory.CreateClient();

        var registry = factory.Services.GetRequiredService<IModelRegistry>();
        foreach (var model in registry.GetAllModels())
        {
            store.SetHealth(new BackendHealth(model.Id, model.Url, false, 503, "refused", DateTimeOffset.UtcNow));
        }

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("probedBackends").GetInt32().Should().BeGreaterThan(0);
        body.GetProperty("healthyBackends").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// A gateway whose registry loaded and holds nothing stays ready, deliberately. Readiness gates
    /// traffic, and a single-replica pod that failed here would be pulled from its Service exactly
    /// when an operator needs the admin console to add the first route — which is the state every
    /// fresh install now starts in, since the release artefact ships no models file.
    /// <c>gateway_models_configured</c> is what reports it instead.
    /// </summary>
    [Fact]
    public async Task GetHealthReady_EmptyButLoadedRegistry_StaysReady()
    {
        // The shape of a fresh install: a database whose route table is empty because no models
        // file was there to seed it — which is now every release, since the artefact ships none.
        // The database branch applies the empty set, so the registry is loaded and holds nothing.
        var store = new BackendHealthStore(Options.Create(new GatewayOptions()));
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            healthStore: store,
            configureSettings: settings =>
                settings["Gateway:ModelsConfigPath"] = "config/no-such-registry.json");
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("registryLoaded").GetBoolean().Should().BeTrue();
        body.GetProperty("configuredBackends").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// The state that is *not* ready, and must not be confused with the one above: the registry
    /// could not be read at all. "No routes configured" is a valid gateway; "the route table failed
    /// to load" is a broken one, and a model count alone cannot tell them apart. Without a database
    /// there is nothing else to load from, so a missing models file leaves the registry unloaded.
    /// </summary>
    [Fact]
    public async Task GetHealthReady_RegistryThatFailedToLoad_Returns503()
    {
        var store = new BackendHealthStore(Options.Create(new GatewayOptions()));
        await using var factory = GatewayWebApplicationFactory.Create(
            healthStore: store,
            configureSettings: settings =>
                settings["Gateway:ModelsConfigPath"] = "config/does-not-exist.json");
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("registryLoaded").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetHealthLive_RemainsPublicAndOk()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
