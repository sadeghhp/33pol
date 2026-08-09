using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase4;

/// <summary>
/// The end-to-end shape of the bug the console was reported for: a model is inferencing, and the
/// operator wants to see it on the dashboard <em>while it runs</em>. Every counter and feed row used
/// to be written at completion and only streaming requests bumped a start-time gauge, so a
/// non-streaming call in progress left `/admin/api/summary` and `/admin/api/requests` reporting an
/// idle gateway for its whole duration.
/// </summary>
public sealed class AdminInFlightRequestsIntegrationTests
{
    [Fact]
    public async Task NonStreamingInference_WhileStillRunning_IsVisibleOnTheDashboard()
    {
        using var upstream = new BlockingUpstreamHandler();
        await using var factory = GatewayWebApplicationFactory.Create(upstreamHandler: upstream);
        var client = factory.CreateClient();

        // Nothing running yet.
        var idle = await ReadSummaryAsync(client);
        idle.GetProperty("activeRequests").GetInt32().Should().Be(0);

        using var content = new StringContent(
            """{"model":"local-mock","stream":false}""", Encoding.UTF8, "application/json");
        var inference = client.PostAsync("/v1/chat/completions", content);

        await upstream.WaitUntilRequestArrivedAsync();

        // ---- the assertions that would all have failed before the fix ----
        var live = await ReadSummaryAsync(client);
        live.GetProperty("activeRequests").GetInt32().Should().Be(1, "the inference is running now");
        live.GetProperty("activeStreams").GetInt32().Should().Be(0, "it is not a streaming request");
        live.GetProperty("activeRequestsPerModel").GetProperty("local-mock").GetInt32().Should().Be(1);
        live.GetProperty("totalInferenceRequests").GetInt64().Should().Be(0, "it has not completed");

        var feed = await ReadRequestsAsync(client);
        var running = feed.EnumerateArray()
            .Single(e => e.GetProperty("modelId").GetString() == "local-mock");
        running.GetProperty("isInFlight").GetBoolean().Should().BeTrue();
        running.GetProperty("statusCode").GetInt32().Should().Be(0, "the upstream has not answered");

        // The duration has to advance between polls, otherwise the row is a frozen placeholder.
        var firstDuration = running.GetProperty("durationMs").GetDouble();
        await Task.Delay(60);
        var secondDuration = (await ReadRequestsAsync(client)).EnumerateArray()
            .Single(e => e.GetProperty("modelId").GetString() == "local-mock")
            .GetProperty("durationMs").GetDouble();
        secondDuration.Should().BeGreaterThan(firstDuration);

        upstream.Release();
        (await inference).StatusCode.Should().Be(HttpStatusCode.OK);

        // ---- and it settles cleanly ----
        var settled = await ReadSummaryAsync(client);
        settled.GetProperty("activeRequests").GetInt32().Should().Be(0);
        settled.GetProperty("totalInferenceRequests").GetInt64().Should().Be(1);
        settled.GetProperty("activeRequestsPerModel").EnumerateObject().Should().BeEmpty();

        var settledFeed = await ReadRequestsAsync(client);
        var completed = settledFeed.EnumerateArray()
            .Where(e => e.GetProperty("modelId").GetString() == "local-mock")
            .ToList();
        completed.Should().HaveCount(1, "the finished row replaces the in-flight one rather than doubling it");
        completed[0].GetProperty("isInFlight").GetBoolean().Should().BeFalse();
        completed[0].GetProperty("statusCode").GetInt32().Should().Be(200);
    }

    private static async Task<JsonElement> ReadSummaryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/admin/api/summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> ReadRequestsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/admin/api/requests?limit=25");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>An upstream that holds the response open until the test lets it answer.</summary>
    private sealed class BlockingUpstreamHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilRequestArrivedAsync() => _arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _arrived.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"id":"c1","object":"chat.completion","choices":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            // Never leave a blocked forward behind if an assertion fails before Release().
            _release.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
