using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Integration.Tests.Support;
using Pol33.Observability.Runtime;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// The Overview's enriched summary: trailing windows and the sparkline series ride alongside the
/// lifetime counters, on both the poll endpoint and the push stream.
/// </summary>
public sealed class AdminOverviewIntegrationTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    [Fact]
    public async Task GetSummary_CarriesWindowsAndSeriesBesideTheLifetimeFields()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var tracker = scope.ServiceProvider.GetRequiredService<IRequestTracker>();
            using var inference = tracker.BeginInferenceRequest("gpt-local", isStreaming: true);
            scope.ServiceProvider.GetRequiredService<IGatewayMetricsCollector>().RecordTimeToFirstToken("gpt-local", 0.1);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");

        json.GetProperty("totalInferenceRequests").GetInt64().Should().Be(1);
        json.GetProperty("averageLatencyMs").ValueKind.Should().Be(JsonValueKind.Number);
        json.GetProperty("requestsPerModel").TryGetProperty("gpt-local", out _).Should().BeTrue();

        var windows = json.GetProperty("windows").EnumerateArray().ToList();
        windows.Select(w => w.GetProperty("window").GetString()).Should().Equal("1m", "5m", "1h", "24h");
        var hour = windows.Single(w => w.GetProperty("window").GetString() == "1h");
        hour.GetProperty("requests").GetInt64().Should().Be(1);
        hour.GetProperty("errorRate").GetDouble().Should().Be(0);
        hour.GetProperty("latencyP95Ms").ValueKind.Should().Be(JsonValueKind.Number);
        hour.GetProperty("ttftSamples").GetInt64().Should().Be(1);
        hour.GetProperty("perModel").EnumerateArray().Single().GetProperty("modelId").GetString().Should().Be("gpt-local");

        var series = json.GetProperty("series");
        series.GetProperty("stepSeconds").GetInt32().Should().Be(60);
        series.GetProperty("points").GetArrayLength().Should().Be(60);
    }

    [Fact]
    public async Task GetLive_FirstFrameCarriesWindows()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/live?limit=5");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        string? dataLine = null;
        while (dataLine is null && !cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line.StartsWith("data:", StringComparison.Ordinal)) dataLine = line;
        }

        dataLine.Should().NotBeNull();
        var summary = JsonDocument.Parse(dataLine!["data:".Length..].Trim()).RootElement.GetProperty("summary");
        summary.GetProperty("windows").GetArrayLength().Should().Be(4);
        summary.GetProperty("series").GetProperty("points").GetArrayLength().Should().Be(60);
    }

    [Fact]
    public async Task GetSummary_WhenWindowedStatsDisabled_OmitsTheSections()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            configureSettings: settings => settings["Gateway:Overview:WindowedStats:Enabled"] = "false");
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");

        json.TryGetProperty("windows", out _).Should().BeFalse();
        json.TryGetProperty("series", out _).Should().BeFalse();
        factory.Services.GetRequiredService<RollingWindowStats>().Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetSummary_CarriesBackendsAndAnEmptyAttentionListWhenHealthy()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");

        var backends = json.GetProperty("backends").EnumerateArray().ToList();
        backends.Should().NotBeEmpty();
        var first = backends[0];
        first.GetProperty("modelId").GetString().Should().NotBeNullOrEmpty();
        first.GetProperty("isHealthy").GetBoolean().Should().BeTrue();
        first.GetProperty("circuitState").GetString().Should().BeOneOf("closed", "unknown");
        first.TryGetProperty("maxConcurrent", out _).Should().BeTrue();
        json.GetProperty("attention").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetSummary_WhenEveryBackendIsDown_ListsACriticalAttentionItem()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            healthStore: new AlwaysUnhealthyBackendHealthStore(),
            configureSettings: settings => settings["Gateway:Overview:Attention:BackendUnhealthyForSeconds"] = "0");
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/summary");

        var attention = json.GetProperty("attention").EnumerateArray().ToList();
        attention.Should().NotBeEmpty();
        var top = attention[0];
        top.GetProperty("code").GetString().Should().Be("no_healthy_backends");
        top.GetProperty("severity").GetString().Should().Be("critical");
        top.GetProperty("link").GetProperty("tab").GetString().Should().Be("routing");
        attention.Should().Contain(i => i.GetProperty("code").GetString() == "backend_unhealthy");
    }

    [Fact]
    public async Task GetBackends_CarriesProbeDetail()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);

        var json = await client.GetFromJsonAsync<JsonElement>("/admin/api/backends");

        var row = json.EnumerateArray().First();
        row.TryGetProperty("lastCheckedUtc", out _).Should().BeTrue();
        row.TryGetProperty("error", out _).Should().BeTrue();
    }
}
