using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Billing.Webhooks;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Tests.Webhooks;

public sealed class BillingWebhookDispatcherTests
{
    [Fact]
    public void ComputeSignature_SameBodyAndSecret_ProducesStableHex()
    {
        var first = BillingWebhookDispatcher.ComputeSignature("{\"a\":1}", "secret");
        var second = BillingWebhookDispatcher.ComputeSignature("{\"a\":1}", "secret");

        first.Should().Be(second);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeSignature_DifferentSecret_ProducesDifferentHex()
    {
        var a = BillingWebhookDispatcher.ComputeSignature("body", "one");
        var b = BillingWebhookDispatcher.ComputeSignature("body", "two");

        a.Should().NotBe(b);
    }

    [Fact]
    public async Task DispatchAsync_WhenNotConfigured_DoesNotSendHttpRequest()
    {
        var handler = new CapturingHttpMessageHandler();
        var dispatcher = CreateDispatcher(handler, new BillingWebhookOptions());

        await dispatcher.DispatchAsync("usage.daily", new { tenantId = Guid.NewGuid() });

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_WhenConfigured_PostsSignedPayload()
    {
        var handler = new CapturingHttpMessageHandler();
        var dispatcher = CreateDispatcher(
            handler,
            new BillingWebhookOptions
            {
                EndpointUrl = "https://hooks.example/33pol",
                Secret = "test-secret",
            });

        await dispatcher.DispatchAsync(
            "quota.warning",
            new { tenantId = Guid.NewGuid(), spend = 80m, limit = 100m });

        handler.Captured.Should().ContainSingle();
        var captured = handler.Captured[0];
        captured.Method.Should().Be(HttpMethod.Post);
        captured.Uri.Should().Be("https://hooks.example/33pol");
        captured.Signature.Should().Be(
            BillingWebhookDispatcher.ComputeSignature(captured.Body, "test-secret"));

        using var json = JsonDocument.Parse(captured.Body);
        json.RootElement.GetProperty("type").GetString().Should().Be("quota.warning");
        json.RootElement.GetProperty("data").GetProperty("spend").GetDecimal().Should().Be(80m);
    }

    private static BillingWebhookDispatcher CreateDispatcher(
        HttpMessageHandler handler,
        BillingWebhookOptions options)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(nameof(BillingWebhookDispatcher))
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        return new BillingWebhookDispatcher(
            factory,
            Options.Create(options),
            NullLogger<BillingWebhookDispatcher>.Instance);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Captured { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var signature = request.Headers.TryGetValues("X-33pol-Signature", out var values)
                ? values.Single()
                : string.Empty;

            Captured.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                body,
                signature));

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Uri,
        string Body,
        string Signature);
}
