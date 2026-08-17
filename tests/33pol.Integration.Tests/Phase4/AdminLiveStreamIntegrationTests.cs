using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase4;

/// <summary>
/// The Overview's push channel. A frame is a server-sent event carrying the summary and the recent
/// feed; the first one arrives immediately, later ones follow activity.
/// </summary>
public sealed class AdminLiveStreamIntegrationTests
{
    [Fact]
    public async Task GetLive_StreamsAnInitialFrameWithSummaryAndRequests()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRecentRequestStore>();
            store.Record(new RecentRequestEntry
            {
                RequestId = "req-live-001",
                Method = "POST",
                Path = "/v1/chat/completions",
                ModelId = "gpt-local",
                CostCenter = "FIN-1",
                StatusCode = 200,
                DurationMs = 12,
                TimestampUtc = DateTimeOffset.UtcNow,
            });
            store.AttachUsage("req-live-001", new RecentRequestUsage(10, 5, 15, "split", 0.001m, 0.002m, 0.003m, "USD", "priced"));
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/api/live?limit=5");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(body);
        string? eventLine = null;
        string? dataLine = null;
        while (dataLine is null && !cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line.StartsWith("event:", StringComparison.Ordinal)) eventLine = line;
            if (line.StartsWith("data:", StringComparison.Ordinal)) dataLine = line;
        }

        eventLine.Should().Be("event: update");
        dataLine.Should().NotBeNull();

        var frame = JsonDocument.Parse(dataLine!["data:".Length..].Trim()).RootElement;
        frame.GetProperty("version").GetInt64().Should().BeGreaterThan(0);
        frame.GetProperty("summary").TryGetProperty("totalInferenceRequests", out _).Should().BeTrue();
        var row = frame.GetProperty("requests").EnumerateArray()
            .Single(r => r.GetProperty("requestId").GetString() == "req-live-001");
        row.GetProperty("costCenter").GetString().Should().Be("FIN-1");
        row.GetProperty("inputCost").GetDecimal().Should().Be(0.001m);
        row.GetProperty("outputCost").GetDecimal().Should().Be(0.002m);
        row.GetProperty("pricingStatus").GetString().Should().Be("priced");
    }

    [Fact]
    public async Task GetLive_WithoutAdminKey_IsRejected()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/admin/api/live", HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
