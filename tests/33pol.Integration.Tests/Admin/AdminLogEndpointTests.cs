using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Diagnostics;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

[Trait("Category", "V1Parity")]
public sealed class AdminLogEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    /// <summary>
    /// Regression test for the wiring bug that made the Logs tab empty in production: the host used
    /// the UseSerilog overload whose writeToProviders defaults to false, so the admin log sink was
    /// constructed and then never called.
    /// </summary>
    [Fact]
    public async Task LoggerWarnings_ReachTheAdminLogBuffer()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        factory.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Pol33.Integration.Tests.SinkProbe")
            .LogWarning("probe warning from the integration test");

        var client = CreateAuthenticatedClient(factory);
        var body = await client.GetStringAsync("/admin/api/logs");

        body.Should().Contain("probe warning from the integration test");
    }

    /// <summary>
    /// The sink's scope stack, plus the request-id middleware that opens it. Without both, the Logs
    /// tab's Request ID column is permanently empty.
    /// </summary>
    [Fact]
    public async Task LogsWrittenDuringARequest_CarryTheRequestId()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory);

        // 'local-mock' is configured against an upstream nothing is listening on, so forwarding
        // fails and the router logs a warning from inside a live request.
        await SendFailingInferenceAsync(factory, client);

        var store = factory.Services.GetRequiredService<IGatewayLogStore>();
        var withRequestId = store.GetRecent(store.Capacity).Where(e => e.RequestId is not null).ToList();

        withRequestId.Should().NotBeEmpty(
            because: "logs raised inside a request must carry the id the request-id middleware scoped in");
        withRequestId.Should().OnlyContain(e => e.RequestId!.StartsWith("req_"));
    }

    /// <summary>
    /// The end-to-end capture path: a real failed forward has to reach the Errors tab with the
    /// model, status and request id attached.
    /// </summary>
    [Fact]
    public async Task AFailedForward_IsCapturedAsATrackedError()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory);

        await SendFailingInferenceAsync(factory, client);

        var writer = factory.Services.GetService<IGatewayErrorArchiveWriter>();
        if (writer is not null)
        {
            await writer.FlushPendingAsync();
        }

        var body = await client.GetStringAsync("/admin/api/errors");

        using var json = JsonDocument.Parse(body);
        // Exactly one: the proxy's detailed record. Serilog's request-completion line and the
        // router's own warning restate the same failure and must not appear beside it.
        json.RootElement.GetProperty("total").GetInt64().Should().Be(1, because: body);
        var occurrence = json.RootElement.GetProperty("occurrences")[0];
        occurrence.GetProperty("source").GetString().Should().Be("proxy");
        occurrence.GetProperty("outcome").GetString().Should().Be("upstream_error");
        occurrence.GetProperty("upstreamTarget").GetString().Should().Be("http://localhost:8080");
        occurrence.GetProperty("hint").GetString().Should().NotBeNullOrWhiteSpace();
        occurrence.GetProperty("modelId").GetString().Should().Be("local-mock");
        occurrence.GetProperty("requestId").GetString().Should().StartWith("req_");
        occurrence.GetProperty("endpointPath").GetString().Should().Be("/v1/chat/completions");
        occurrence.GetProperty("statusCode").GetInt32().Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task ListLogs_ReportsTheMatchedTotalSeparatelyFromThePage()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Pol33.Probe");
        for (var i = 0; i < 6; i++)
        {
            logger.LogWarning("distinct probe warning {Index}", i);
        }

        var client = CreateAuthenticatedClient(factory);
        var body = await client.GetStringAsync("/admin/api/logs?limit=2");

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("count").GetInt32().Should().Be(2);
        // Without this the UI cannot tell "2 of 6" from "2 of 2", and its truncation warning fires
        // whenever a page happens to be exactly full.
        json.RootElement.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(6);
    }

    [Fact]
    public async Task ClearLogs_ReportsHowManyWereRemoved()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Pol33.Probe").LogWarning("probe before clear");

        var client = CreateAuthenticatedClient(factory);
        var response = await client.DeleteAsync("/admin/api/logs");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("entriesCleared").GetInt32().Should().BeGreaterThan(0);

        var after = await client.GetStringAsync("/admin/api/logs");
        JsonDocument.Parse(after).RootElement.GetProperty("total").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task KestrelNoise_IsKeptOutOfTheBuffer()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        // Every dropped client connection logs one of these. Left in, they alone would evict every
        // real diagnostic from the ring.
        factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Server.Kestrel.Connections")
            .LogWarning("the connection was reset by the client");

        var store = factory.Services.GetRequiredService<IGatewayLogStore>();

        store.GetRecent(store.Capacity)
            .Should().NotContain(e => e.Message.Contains("reset by the client"));
    }

    [Fact]
    public async Task ScopeKeys_AreSharedBetweenTheMiddlewareAndTheSink()
    {
        // Both ends read the same constant; a literal on either side fails silently.
        GatewayLogScopeKeys.RequestId.Should().Be("GatewayRequestId");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Mints an inference-capable key and calls a model whose upstream is not listening. The
    /// bootstrap admin key carries the Admin role only, so it is rejected before it ever reaches
    /// the router.
    /// </summary>
    internal static async Task SendFailingInferenceAsync(
        WebApplicationFactory<Program> factory,
        HttpClient adminClient)
    {
        var created = await adminClient.PostAsync(
            "/admin/api/keys",
            new StringContent("""{"role":"Both"}""", System.Text.Encoding.UTF8, "application/json"));
        var createdBody = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, because: createdBody);

        using var createdJson = JsonDocument.Parse(createdBody);
        var keyId = createdJson.RootElement.GetProperty("id").GetGuid();
        var inferenceKey = createdJson.RootElement.GetProperty("secret").GetString();

        // A new key is granted nothing by default, so the model has to be allowed explicitly or the
        // request is rejected before it ever reaches the router.
        var granted = await adminClient.PutAsync(
            $"/admin/api/keys/{keyId}/model-grants",
            new StringContent(
                """{"modelIds":["local-mock"]}""",
                System.Text.Encoding.UTF8,
                "application/json"));
        granted.StatusCode.Should().Be(HttpStatusCode.OK, because: await granted.Content.ReadAsStringAsync());

        var inferenceClient = factory.CreateClient();
        inferenceClient.DefaultRequestHeaders.Add("X-API-Key", inferenceKey);

        var inference = await inferenceClient.PostAsync(
            "/v1/chat/completions",
            new StringContent(
                """{"model":"local-mock","messages":[{"role":"user","content":"hi"}]}""",
                System.Text.Encoding.UTF8,
                "application/json"));

        // The call must fail at the upstream, not earlier: a 401/403/404 would mean the request
        // never reached the router and the test would be asserting nothing.
        var inferenceBody = await inference.Content.ReadAsStringAsync();
        ((int)inference.StatusCode).Should().BeGreaterThanOrEqualTo(500, because: inferenceBody);
    }

    internal static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
