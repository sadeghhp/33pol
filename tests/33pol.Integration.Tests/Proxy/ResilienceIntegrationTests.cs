using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pol33.Core.Abstractions;
using Pol33.Integration.Tests.Support;

namespace Pol33.Integration.Tests.Proxy;

public sealed class ResilienceIntegrationTests
{
    [Fact]
    public async Task PostInference_WhenDraining_Returns503GatewayDraining()
    {
        await using var factory = GatewayWebApplicationFactory.Create();
        using var scope = factory.Services.CreateScope();
        var drain = scope.ServiceProvider.GetRequiredService<IGatewayDrainState>();
        drain.BeginDrain();

        var client = factory.CreateClient();
        using var content = new StringContent(
            """{"model":"gpt-local","stream":false}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryGetValues("X-33pol-Error-Code", out var codes).Should().BeTrue();
        codes!.Single().Should().Be("gateway_draining");
    }

    [Fact]
    public async Task PostInference_ContentLengthOverLimit_Returns400RequestTooLarge()
    {
        await using var factory = GatewayWebApplicationFactory.Create(configureConfiguration: config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Resilience:MaxRequestBodyBytes"] = "10",
            });
        });

        var client = factory.CreateClient();
        using var content = new StringContent(
            """{"model":"local-mock","stream":false,"prompt":"too-large"}""",
            Encoding.UTF8,
            "application/json");
        content.Headers.ContentLength = 100;
        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.TryGetValues("X-33pol-Error-Code", out var codes).Should().BeTrue();
        codes!.Single().Should().Be("request_too_large");
    }

    /// <summary>
    /// An unhandled failure anywhere in the pipeline is answered with the documented error body.
    /// </summary>
    /// <remarks>
    /// <para>The pipeline had no terminal handler, so anything it did not catch escaped to the server
    /// and was answered with a bare status line — no body, no <c>error.code</c>, no
    /// <c>X-33pol-Error-Code</c>. This test's value is proving the handler is actually registered in
    /// the real pipeline; the mapping itself is pinned by
    /// <c>GatewayExceptionHandlingMiddlewareTests</c>.</para>
    ///
    /// <para>The oversized-chunked-body case that motivated the handler cannot be driven from here:
    /// this harness is an in-memory TestServer, which does not implement Kestrel's
    /// <c>IHttpMaxRequestBodySizeFeature</c>, so the mid-read rejection never fires. That mapping is
    /// covered at unit level instead.</para>
    /// </remarks>
    [Fact]
    public async Task PostInference_UnhandledUpstreamFailure_ReturnsOpenAiShapedError()
    {
        await using var factory = GatewayWebApplicationFactory.Create(
            upstreamHandler: new ThrowingUpstreamHandler());

        var client = factory.CreateClient();
        using var content = new StringContent(
            """{"model":"local-mock","stream":false}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Headers.TryGetValues("X-33pol-Error-Code", out var codes).Should().BeTrue();
        codes!.Single().Should().Be("upstream_error");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("upstream_error", "the error must carry the documented OpenAI-shaped body");
    }

    /// <summary>Fails in a way the forwarder's own catch clauses deliberately do not cover.</summary>
    private sealed class ThrowingUpstreamHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated unhandled failure.");
    }
}
