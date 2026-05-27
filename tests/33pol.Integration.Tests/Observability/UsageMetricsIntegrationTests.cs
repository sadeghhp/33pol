using System.Net;
using System.Text;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Observability;

public sealed class UsageMetricsIntegrationTests
{
    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task PostChatCompletions_WithUpstreamUsage_IncrementsTokenMetrics()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"gpt-local","stream":false}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await response.Content.ReadAsStringAsync();

        await Task.Delay(300);

        var metricsResponse = await client.GetAsync("/metrics");
        metricsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var metrics = await metricsResponse.Content.ReadAsStringAsync();

        metrics.Should().Contain("gateway_tokens_total");
        metrics.Should().Contain("direction=\"input\"");
        metrics.Should().Contain("direction=\"output\"");
    }

    [Fact]
    public async Task PostChatCompletions_ExposesRoutingAndPolicyMetrics()
    {
        var handler = new MockUpstreamHandler();
        using var factory = GatewayWebApplicationFactory.Create(handler);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/chat/completions",
            JsonBody("""{"model":"gpt-local","stream":false}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await response.Content.ReadAsStringAsync();

        var metrics = await client.GetStringAsync("/metrics");

        metrics.Should().Contain("gateway_inference_route_total");
        metrics.Should().Contain("gateway_forward_attempts_total");
        metrics.Should().Contain("gateway_model_resolve_total");
        metrics.Should().Contain("gateway_circuit_breaker_state");
        metrics.Should().Contain("gateway_bulkhead_inflight");
    }
}
