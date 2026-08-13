using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

/// <summary>
/// Covers the path the unit tests cannot: a real failing inference request, through the real
/// middleware and DI graph, arriving in the Errors tab's own query with the tab's own time window.
/// </summary>
public sealed class AdminErrorCapturePipelineTests
{
    [Fact]
    public async Task FailedInference_AppearsInTheErrorsGridWithinTheDefaultWindow()
    {
        var handler = new ThrowingUpstreamHandler();
        await using var factory = GatewayWebApplicationFactory.Create(upstreamHandler: handler);
        var client = factory.CreateClient();

        using var content = new StringContent(
            """{"model":"local-mock","stream":false}""",
            Encoding.UTF8,
            "application/json");
        var inference = await client.PostAsync("/v1/chat/completions", content);
        inference.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-24).ToString("O"));
        var response = await client.GetAsync($"/admin/api/errors/groups?from={from}&limit=50&offset=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("total").GetInt64().Should().BeGreaterThan(0);
        body.GetProperty("groups").EnumerateArray().Should().Contain(g =>
            g.GetProperty("modelId").GetString() == "local-mock");
    }

    /// <summary>
    /// The unfiltered stored total is what lets an empty grid distinguish "the window is hiding
    /// rows" from "nothing was ever captured" — the two need opposite responses from an operator,
    /// and the windowed totals alone cannot tell them apart.
    /// </summary>
    [Fact]
    public async Task GetGroups_ReportsTheStoredTotalIndependentlyOfTheTimeWindow()
    {
        var handler = new ThrowingUpstreamHandler();
        await using var factory = GatewayWebApplicationFactory.Create(upstreamHandler: handler);
        var client = factory.CreateClient();

        using var content = new StringContent(
            """{"model":"local-mock","stream":false}""",
            Encoding.UTF8,
            "application/json");
        (await client.PostAsync("/v1/chat/completions", content)).StatusCode
            .Should().Be(HttpStatusCode.BadGateway);

        // A window that deliberately excludes the failure just recorded.
        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-29).ToString("O"));
        var response = await client.GetAsync($"/admin/api/errors/groups?from={from}&to={to}");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("total").GetInt64().Should().Be(0);
        body.GetProperty("occurrenceTotal").GetInt64().Should().Be(0);
        body.GetProperty("storedTotal").GetInt64().Should().BeGreaterThan(0);
    }

    private sealed class ThrowingUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("upstream unavailable");
    }
}
