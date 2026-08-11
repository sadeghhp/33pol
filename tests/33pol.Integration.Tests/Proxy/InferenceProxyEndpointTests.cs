using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

[Trait("Category", "V1Parity")]
public sealed class InferenceProxyEndpointTests
{
    [Fact]
    public async Task PostChatCompletions_NonStream_Returns200FromMockUpstream()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"gpt-local","stream":false}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.SendCount.Should().Be(1);
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("http://localhost:8080/v1/chat/completions");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("upstream-1");
    }

    [Fact]
    public async Task PostChatCompletions_AliasModel_RewritesCanonicalIdForUpstream()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"gpt-local","stream":false}"""));

        handler.LastRequestBody.Should().Contain("\"model\":\"local-mock\"");
        handler.LastRequestBody.Should().NotContain("gpt-local");
    }

    /// <summary>
    /// The alias rewrite splices the model token out of the buffered body, so a large prompt must
    /// reach the upstream byte for byte rather than being rebuilt.
    /// </summary>
    /// <remarks>
    /// The rewrite this replaced read the body into a string, re-parsed it into a JsonDocument, wrote
    /// it back through a growing MemoryStream, copied that with ToArray, decoded it and re-encoded it
    /// — roughly thirteen times the body size in Large Object Heap allocations per aliased request,
    /// plus a UTF-16 round trip that silently rewrote anything that was not well-formed UTF-8.
    /// </remarks>
    [Fact]
    public async Task PostChatCompletions_LargeAliasedBody_ReachesUpstreamIntact()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var prompt = string.Concat(Enumerable.Repeat(@"héllo ✓ {}[]\"" ", 60_000));
        var request = $$"""{"model":"gpt-local","stream":false,"messages":[{"role":"user","content":"{{prompt}}"}]}""";

        var response = await client.PostAsync("/v1/chat/completions", JsonBody(request));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.LastRequestBody.Should().Be(
            request.Replace("\"gpt-local\"", "\"local-mock\"", StringComparison.Ordinal),
            "only the model token may differ from what the client sent");
    }

    [Fact]
    public async Task PostChatCompletions_Stream_SetsSseResponseHeaders()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"local-mock","stream":true}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl?.ToString().Should().Be("no-cache");
        response.Headers.Contains("X-Accel-Buffering").Should().BeTrue();

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("data:");
    }

    [Fact]
    public async Task PostChatCompletions_Stream_ForwardsFirstChunkBeforeUpstreamDelay()
    {
        var handler = new DelayedChunkStreamingHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonBody("""{"model":"local-mock","stream":true}"""),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var bodyStream = await response.Content.ReadAsStreamAsync();

        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var firstByteBuffer = new byte[1];
        var firstRead = bodyStream.ReadAsync(firstByteBuffer, readCts.Token).AsTask();
        var winner = await Task.WhenAny(
            firstRead,
            Task.Delay(DelayedChunkStreamingHandler.InterChunkDelay / 2, readCts.Token));

        winner.Should().Be(firstRead, "first SSE chunk should reach the client before upstream inter-chunk delay");
        (await firstRead).Should().BeGreaterThan(0);

        using var reader = new StreamReader(bodyStream);
        var remainder = await reader.ReadToEndAsync(readCts.Token);
        remainder.Should().Contain(DelayedChunkStreamingHandler.SecondChunkMarker);
        remainder.Should().Contain("[DONE]");
    }

    [Fact]
    public async Task PostChatCompletions_Stream_ConcurrentClients_ReceiveFirstChunkQuickly()
    {
        var handler = new DelayedChunkStreamingHandler();
        using var factory = GatewayWebApplicationFactory.Create(
            handler,
            configureSettings: settings =>
            {
                settings["RateLimiting:Default:MaxConcurrentStreams"] = "16";
                settings["RateLimiting:Default:Rpm"] = "600";
                settings["RateLimiting:Default:Burst"] = "32";
            });
        using var client = factory.CreateClient();

        var tasks = Enumerable.Range(0, 6).Select(async _ =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = JsonBody("""{"model":"local-mock","stream":true}"""),
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await using var bodyStream = await response.Content.ReadAsStreamAsync();

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var firstRead = bodyStream.ReadAsync(new byte[1], readCts.Token).AsTask();
            var winner = await Task.WhenAny(
                firstRead,
                Task.Delay(DelayedChunkStreamingHandler.InterChunkDelay / 2, readCts.Token));

            winner.Should().Be(firstRead, "streaming response should flush first chunk without waiting for next upstream chunk");
            (await firstRead).Should().BeGreaterThan(0);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task PostCompletions_ForwardsToBackendPath()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/completions",
            JsonBody("""{"model":"local-mock","prompt":"hello"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v1/completions");
    }

    [Fact]
    public async Task PostEmbeddings_ForwardsToBackendPath()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/embeddings",
            JsonBody("""{"model":"local-mock","input":"hello"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v1/embeddings");
    }

    [Fact]
    public async Task PostRerank_ForwardsToBackendPath()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/rerank",
            JsonBody("""{"model":"local-mock","query":"test","documents":["doc1"]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/v1/rerank");
    }

    [Fact]
    public async Task PostRerank_AliasModel_RewritesCanonicalIdForUpstream()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        await client.PostAsync(
            "/v1/rerank",
            JsonBody("""{"model":"gpt-local","query":"test","documents":["doc1"]}"""));

        handler.LastRequestBody.Should().Contain("\"model\":\"local-mock\"");
        handler.LastRequestBody.Should().NotContain("gpt-local");
    }

    [Fact]
    public async Task PostChatCompletions_UnknownModel_Returns404()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"does-not-exist"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        handler.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task PostChatCompletions_UnhealthyBackend_Returns502()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(
            handler,
            new AlwaysUnhealthyBackendHealthStore());
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"local-mock"}"""));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        handler.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task GetMetrics_IsPassthrough_DoesNotInvokeUpstream()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.SendCount.Should().Be(0);
        (await response.Content.ReadAsStringAsync()).Should().Contain("# HELP");
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");
}
