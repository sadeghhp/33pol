using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Integration.Tests.Infrastructure;

namespace Pol33.Integration.Tests.V1Parity;

[Trait("Category", "V1Parity")]
public sealed class InferenceV1ParityTests
{
    [Fact]
    public async Task PostChatCompletions_NonStream_Returns200FromMockUpstream()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();
        using var content = JsonContent.Create(new { model = "canonical-model", stream = false });

        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Hello from mock upstream");
        factory.Upstream.RequestCount.Should().Be(1);
        factory.Upstream.LastRequestPath.Should().Contain("/v1/chat/completions");
    }

    [Fact]
    public async Task PostChatCompletions_Stream_ReturnsSseChunks()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();
        using var content = JsonContent.Create(new { model = "canonical-model", stream = true });

        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("data:");
        body.Should().Contain("[DONE]");
        response.Headers.CacheControl?.ToString().Should().Contain("no-cache");
    }

    [Fact]
    public async Task PostCompletions_NonStream_Returns200()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();
        using var content = JsonContent.Create(new { model = "canonical-model", stream = false, prompt = "Hi" });

        var response = await client.PostAsync("/v1/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Upstream.LastRequestPath.Should().Contain("/v1/completions");
    }

    [Fact]
    public async Task PostEmbeddings_Returns200()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();
        using var content = JsonContent.Create(new { model = "canonical-model", input = "hello" });

        var response = await client.PostAsync("/v1/embeddings", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("embedding");
        factory.Upstream.LastRequestPath.Should().Contain("/v1/embeddings");
    }

    [Fact]
    public async Task PostChatCompletions_WithAlias_RewritesModelInUpstreamBody()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();
        using var content = JsonContent.Create(new { model = "alias-model", stream = false });

        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Upstream.LastRequestBody.Should().Contain("canonical-model");
        factory.Upstream.LastRequestBody.Should().NotContain("alias-model");
    }

    [Fact]
    public async Task PostChatCompletions_UnknownModel_Returns404()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();
        using var content = JsonContent.Create(new { model = "does-not-exist", stream = false });

        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Upstream.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task PostChatCompletions_UnhealthyBackend_Returns502()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();

        var scope = factory.Services.CreateScope();
        var health = scope.ServiceProvider.GetRequiredService<IBackendHealthStore>();
        health.SetHealth(new BackendHealth(
            "canonical-model",
            "http://mock-upstream.local",
            IsHealthy: false,
            StatusCode: 503,
            Error: "probe failed",
            DateTimeOffset.UtcNow));

        using var content = JsonContent.Create(new { model = "canonical-model", stream = false });
        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        factory.Upstream.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task PostMetricsPath_IsNotProxiedToUpstream()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Upstream.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task GetAdminConfigStatus_IsNotProxiedToUpstream()
    {
        using var factory = new GatewayWebApplicationFactory();
        factory.Upstream.Reset();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/api/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Upstream.RequestCount.Should().Be(0);
    }
}
