using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Admin;

[Trait("Category", "V1Parity")]
public sealed class AdminErrorEndpointTests
{
    private const string AdminKey = "sk-33pol-integration-admin-key";

    [Theory]
    [InlineData("/admin/api/errors/groups")]
    [InlineData("/admin/api/errors")]
    [InlineData("/admin/api/errors/facets")]
    [InlineData("/admin/api/errors/export")]
    public async Task Endpoints_WithoutAuth_Return401(string path)
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteErrors_WithoutAuth_Returns401()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = factory.CreateClient();

        var response = await client.DeleteAsync("/admin/api/errors?confirm=true");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetGroups_ReturnsCamelCaseEnvelope()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        await RecordErrorsAsync(factory, Error("req_1"), Error("req_2"));
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.GetAsync("/admin/api/errors/groups");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("total").GetInt64().Should().Be(1);
        root.GetProperty("occurrenceTotal").GetInt64().Should().Be(2);
        root.GetProperty("persisted").GetBoolean().Should().BeTrue();
        root.GetProperty("source").GetString().Should().Be("database");

        var group = root.GetProperty("groups")[0];
        group.GetProperty("count").GetInt64().Should().Be(2);
        group.GetProperty("modelId").GetString().Should().Be("gpt-4o");
        group.GetProperty("statusCode").GetInt32().Should().Be(502);
        group.GetProperty("fingerprint").GetString().Should().NotBeNullOrWhiteSpace();
        group.GetProperty("lastRequestId").GetString().Should().NotBeNullOrWhiteSpace();
        group.GetProperty("hint").GetString().Should().NotBeNullOrWhiteSpace();

        // Source and Category are fingerprint components, so a group cannot mix them. They are what
        // tells the detail panel whether blank request fields mean "not captured" or "never had a
        // request" — a startup failure and a dropped proxy field look identical without them.
        group.GetProperty("source").GetString().Should().Be(GatewayErrorSourceNames.Proxy);
        group.GetProperty("category").GetString().Should().Be("ModelRouterMiddleware");
    }

    [Fact]
    public async Task GetOccurrences_FiltersByFingerprintAndReturnsTheStackTrace()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        await RecordErrorsAsync(factory, Error("req_1"), Error("req_2", message: "a different failure"));
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var groupsBody = await client.GetStringAsync("/admin/api/errors/groups");
        using var groupsJson = JsonDocument.Parse(groupsBody);
        var fingerprint = groupsJson.RootElement.GetProperty("groups")[0].GetProperty("fingerprint").GetString();

        var body = await client.GetStringAsync($"/admin/api/errors?fingerprint={fingerprint}");

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("total").GetInt64().Should().Be(1);
        var occurrence = json.RootElement.GetProperty("occurrences")[0];
        occurrence.GetProperty("stackTrace").GetString().Should().Contain("Pol33");
        occurrence.GetProperty("requestId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetOccurrence_ById_ReturnsTheRecordOr404()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        await RecordErrorsAsync(factory, Error("req_1"));
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var listBody = await client.GetStringAsync("/admin/api/errors");
        using var listJson = JsonDocument.Parse(listBody);
        var id = listJson.RootElement.GetProperty("occurrences")[0].GetProperty("id").GetString();

        var found = await client.GetAsync($"/admin/api/errors/{id}");
        var missing = await client.GetAsync("/admin/api/errors/err_does_not_exist");

        found.StatusCode.Should().Be(HttpStatusCode.OK);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetFacets_ReturnsTheValuesActuallyPresent()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        await RecordErrorsAsync(factory, Error("req_1"), Error("req_2", message: "other") with { ModelId = "claude" });
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var body = await client.GetStringAsync("/admin/api/errors/facets");

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("models").GetArrayLength().Should().Be(2);
        json.RootElement.GetProperty("statusCodes").GetArrayLength().Should().BeGreaterThan(0);
        json.RootElement.GetProperty("levels").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetGroups_ClampsLimitAndOffset()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var body = await client.GetStringAsync("/admin/api/errors/groups?limit=99999&offset=-5");

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("limit").GetInt32().Should().Be(GatewayErrorQuery.MaxLimit);
        json.RootElement.GetProperty("offset").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Export_AsCsv_QuotesCellsAndNeutralizesFormulas()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        // An upstream controls its own error body, so a message beginning with '=' is a real
        // spreadsheet-injection vector rather than a formality.
        await RecordErrorsAsync(factory, Error("req_1", message: "=cmd|'/c calc'!A1"));
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.GetAsync("/admin/api/errors/export?format=csv");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        body.Should().StartWith("timestampUtc,level,source,category");
        body.Should().Contain("\"'=cmd");
    }

    [Fact]
    public async Task Export_DefaultsToJson()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        await RecordErrorsAsync(factory, Error("req_1"));
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var body = await client.GetStringAsync("/admin/api/errors/export");

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("occurrences").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task DeleteErrors_WithoutConfirm_Returns400AndChangesNothing()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
        await RecordErrorsAsync(factory, Error("req_1"));
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.DeleteAsync("/admin/api/errors");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, because: body);
        body.Should().Contain("confirmation_required");

        var remaining = await client.GetStringAsync("/admin/api/errors");
        JsonDocument.Parse(remaining).RootElement.GetProperty("total").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task DeleteErrors_ClearsRecordsCountersAndTheRecentRequestFeed()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase(AdminKey);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<Pol33.Observability.Runtime.GatewayRuntimeState>();
            runtime.RecordRequestComplete("gpt-4o", success: true, durationMs: 50, wasStreaming: false);
            runtime.RecordRequestComplete("gpt-4o", success: false, durationMs: 100, wasStreaming: false);
            runtime.EnqueueRecent(new RecentRequestEntry
            {
                RequestId = "req_ok",
                Method = "POST",
                Path = "/v1/chat/completions",
                ModelId = "gpt-4o",
                StatusCode = 200,
                TimestampUtc = DateTimeOffset.UtcNow,
            });
            runtime.EnqueueRecent(new RecentRequestEntry
            {
                RequestId = "req_bad",
                Method = "POST",
                Path = "/v1/chat/completions",
                ModelId = "gpt-4o",
                StatusCode = 502,
                ErrorCode = "upstream_error",
                TimestampUtc = DateTimeOffset.UtcNow,
            });
        }

        await RecordErrorsAsync(factory, Error("req_bad"));
        var client = CreateAuthenticatedClient(factory, AdminKey);

        var response = await client.DeleteAsync("/admin/api/errors?confirm=true");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: body);
        using (var clearJson = JsonDocument.Parse(body))
        {
            clearJson.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            clearJson.RootElement.GetProperty("totalErrorsCleared").GetInt64().Should().Be(1);
            clearJson.RootElement.GetProperty("recentRequestRowsRemoved").GetInt32().Should().Be(1);
            clearJson.RootElement.GetProperty("snapshotRewritten").GetBoolean().Should().BeTrue();
        }

        var summary = await client.GetStringAsync("/admin/api/summary");
        using (var summaryJson = JsonDocument.Parse(summary))
        {
            summaryJson.RootElement.GetProperty("totalErrors").GetInt64().Should().Be(0);
            summaryJson.RootElement.GetProperty("errorsPerModel").EnumerateObject().Should().BeEmpty();
            // Clearing errors must not rewrite the throughput history alongside them.
            summaryJson.RootElement.GetProperty("totalInferenceRequests").GetInt64().Should().Be(2);
        }

        var requests = await client.GetStringAsync("/admin/api/requests");
        requests.Should().NotContain("req_bad");
        requests.Should().Contain("req_ok");

        var errors = await client.GetStringAsync("/admin/api/errors");
        JsonDocument.Parse(errors).RootElement.GetProperty("total").GetInt64().Should().Be(0);
    }

    /// <summary>
    /// The whole point of rewriting the persisted snapshot: a clear that only touches memory looks
    /// like it worked until the next restart brings every count back.
    /// </summary>
    [Fact]
    public async Task DeleteErrors_SurvivesAGatewayRestart()
    {
        var databaseName = Guid.NewGuid().ToString("N");

        await using (var factory = CreateSharedDatabaseFactory(databaseName))
        {
            await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
            using (var scope = factory.Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<Pol33.Observability.Runtime.GatewayRuntimeState>()
                    .RecordRequestComplete("gpt-4o", success: false, durationMs: 100, wasStreaming: false);
            }

            await RecordErrorsAsync(factory, Error("req_1"));

            var client = CreateAuthenticatedClient(factory, AdminKey);
            var cleared = await client.DeleteAsync("/admin/api/errors?confirm=true");
            cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await using var restarted = CreateSharedDatabaseFactory(databaseName);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(restarted);
        var restartedClient = CreateAuthenticatedClient(restarted, AdminKey);

        var summary = await restartedClient.GetStringAsync("/admin/api/summary");
        JsonDocument.Parse(summary).RootElement.GetProperty("totalErrors").GetInt64().Should().Be(0);

        var errors = await restartedClient.GetStringAsync("/admin/api/errors");
        JsonDocument.Parse(errors).RootElement.GetProperty("total").GetInt64().Should().Be(0);
    }

    /// <summary>
    /// scope=all resets the whole counter snapshot, not just the error half. The in-memory counters
    /// have to be reset too: they are what the snapshot is exported from, so writing a zeroed row
    /// while memory still held the old totals would last only until the next periodic flush.
    /// </summary>
    [Fact]
    public async Task DeleteErrors_WithScopeAll_ResetsEveryCounterDurably()
    {
        var databaseName = Guid.NewGuid().ToString("N");

        await using (var factory = CreateSharedDatabaseFactory(databaseName))
        {
            await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
            using (var scope = factory.Services.CreateScope())
            {
                var runtime = scope.ServiceProvider
                    .GetRequiredService<Pol33.Observability.Runtime.GatewayRuntimeState>();
                runtime.RecordRequestComplete("gpt-4o", success: true, durationMs: 50, wasStreaming: false);
                runtime.RecordRequestComplete("gpt-4o", success: false, durationMs: 100, wasStreaming: false);
            }

            var client = CreateAuthenticatedClient(factory, AdminKey);
            var cleared = await client.DeleteAsync("/admin/api/errors?confirm=true&scope=all");
            cleared.StatusCode.Should().Be(HttpStatusCode.OK, because: await cleared.Content.ReadAsStringAsync());

            var summary = await client.GetStringAsync("/admin/api/summary");
            using var summaryJson = JsonDocument.Parse(summary);
            summaryJson.RootElement.GetProperty("totalErrors").GetInt64().Should().Be(0);
            summaryJson.RootElement.GetProperty("totalInferenceRequests").GetInt64().Should().Be(0);
        }

        await using var restarted = CreateSharedDatabaseFactory(databaseName);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(restarted);
        var restartedClient = CreateAuthenticatedClient(restarted, AdminKey);

        var afterRestart = await restartedClient.GetStringAsync("/admin/api/summary");
        using var afterJson = JsonDocument.Parse(afterRestart);
        afterJson.RootElement.GetProperty("totalErrors").GetInt64().Should().Be(0);
        afterJson.RootElement.GetProperty("totalInferenceRequests").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task RecordedErrors_SurviveARestartWhenNotCleared()
    {
        var databaseName = Guid.NewGuid().ToString("N");

        await using (var factory = CreateSharedDatabaseFactory(databaseName))
        {
            await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);
            await RecordErrorsAsync(factory, Error("req_1"));
        }

        await using var restarted = CreateSharedDatabaseFactory(databaseName);
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(restarted);
        var client = CreateAuthenticatedClient(restarted, AdminKey);

        var body = await client.GetStringAsync("/admin/api/errors");

        JsonDocument.Parse(body).RootElement.GetProperty("total").GetInt64().Should().Be(1);
    }

    private static WebApplicationFactory<Program> CreateSharedDatabaseFactory(string databaseName) =>
        GatewayWebApplicationFactory.CreateWithInMemoryDatabase(
            AdminKey,
            configureSettings: settings => settings["ConnectionStrings:GatewayDb"] = $"InMemory:{databaseName}");

    /// <summary>
    /// Records through the live store and flushes the batch writer, so the assertions run against
    /// the same database path production uses rather than a hand-seeded table.
    /// </summary>
    private static async Task RecordErrorsAsync(
        WebApplicationFactory<Program> factory,
        params GatewayErrorRecord[] records)
    {
        var store = factory.Services.GetRequiredService<IGatewayErrorStore>();
        foreach (var record in records)
        {
            store.Record(record);
        }

        var writer = factory.Services.GetService<IGatewayErrorArchiveWriter>();
        if (writer is not null)
        {
            await writer.FlushPendingAsync();
        }
    }

    private static GatewayErrorRecord Error(
        string requestId,
        string message = "Upstream returned 502 for model 'gpt-4o'.") => new()
    {
        Id = $"err_{Guid.NewGuid():N}",
        Fingerprint = string.Empty,
        OccurredAt = DateTimeOffset.UtcNow,
        Level = "Error",
        Source = GatewayErrorSourceNames.Proxy,
        Category = "ModelRouterMiddleware",
        EventCode = "upstream_error",
        Message = message,
        ExceptionType = "System.Net.Http.HttpRequestException",
        StackTrace = "at Pol33.Proxy.Middleware.ModelRouterMiddleware.InvokeAsync()",
        Method = "POST",
        Path = "/v1/chat/completions",
        RouteKind = "chat",
        StatusCode = 502,
        ModelId = "gpt-4o",
        UpstreamTarget = "http://upstream:8000",
        Outcome = "upstream_5xx",
        RequestId = requestId,
        DurationMs = 123,
        Hint = "The upstream failed internally. Check its own logs.",
    };

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
