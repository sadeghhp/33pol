using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Phase4;

public sealed class AdminRequestsIntegrationTests
{
    [Fact]
    public async Task GetRequests_ReturnsErrorCode_WhenRecordedInStore()
    {
        await using var factory = GatewayWebApplicationFactory.CreateWithInMemoryDatabase();
        await GatewayWebApplicationFactory.EnsureAuthReadyAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRecentRequestStore>();
            store.Record(new RecentRequestEntry
            {
                RequestId = "req-error-001",
                Method = "POST",
                Path = "/v1/chat/completions",
                ModelId = "gpt-local",
                StatusCode = 502,
                DurationMs = 42,
                IsStreaming = false,
                ErrorCode = "upstream_error",
                TimestampUtc = DateTimeOffset.UtcNow,
            });
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", "sk-33pol-integration-admin-key");

        var response = await client.GetAsync("/admin/api/requests?limit=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.ValueKind.Should().Be(JsonValueKind.Array);
        json.EnumerateArray().Should().Contain(entry =>
            entry.GetProperty("requestId").GetString() == "req-error-001" &&
            entry.GetProperty("errorCode").GetString() == "upstream_error");
    }

    [Fact]
    public async Task PostInference_ForwardFailure_IncludesErrorCodeInRecentRequests()
    {
        var handler = new ThrowingUpstreamHandler();
        await using var factory = GatewayWebApplicationFactory.Create(upstreamHandler: handler);

        var client = factory.CreateClient();
        using var content = new StringContent(
            """{"model":"local-mock","stream":false}""",
            Encoding.UTF8,
            "application/json");
        var inferenceResponse = await client.PostAsync("/v1/chat/completions", content);
        inferenceResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        var adminResponse = await client.GetAsync("/admin/api/requests?limit=10");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await adminResponse.Content.ReadFromJsonAsync<JsonElement>();
        json.EnumerateArray().Should().Contain(entry =>
            entry.GetProperty("modelId").GetString() == "local-mock" &&
            entry.GetProperty("errorCode").GetString() == "upstream_error");
    }

    private sealed class ThrowingUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("upstream unavailable");
    }
}
