using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pol33.Billing.Webhooks;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Tests.Webhooks;

/// <summary>
/// The dispatcher only queues. Delivery is <see cref="BillingWebhookSenderHostedService"/>'s job, so
/// that a slow receiver cannot stall the caller — which, on the usage-persistence path, is the
/// single-reader billing writer.
/// </summary>
public sealed class BillingWebhookDispatcherTests
{
    private static readonly BillingWebhookOptions Configured = new()
    {
        EndpointUrl = "https://hooks.example/33pol",
        Secret = "test-secret",
    };

    [Fact]
    public void ComputeSignature_SameInputs_ProducesStableValue()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        var first = BillingWebhookDispatcher.ComputeSignature("{\"a\":1}", "secret", at);
        var second = BillingWebhookDispatcher.ComputeSignature("{\"a\":1}", "secret", at);

        first.Should().Be(second);
        first.Should().MatchRegex("^t=1700000000,v1=[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeSignature_DifferentSecret_ProducesDifferentValue()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        BillingWebhookDispatcher.ComputeSignature("body", "one", at)
            .Should().NotBe(BillingWebhookDispatcher.ComputeSignature("body", "two", at));
    }

    /// <summary>
    /// The timestamp is signed, not merely sent alongside. Signing the body alone left every
    /// delivery replayable forever, because a receiver verifying the signature could not distinguish
    /// a fresh event from a captured one.
    /// </summary>
    [Fact]
    public void ComputeSignature_DifferentTimestamp_ProducesDifferentValue()
    {
        var a = BillingWebhookDispatcher.ComputeSignature(
            "body", "secret", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var b = BillingWebhookDispatcher.ComputeSignature(
            "body", "secret", DateTimeOffset.FromUnixTimeSeconds(1_700_000_001));

        a.Should().NotBe(b);
    }

    [Fact]
    public async Task DispatchAsync_WhenNotConfigured_QueuesNothing()
    {
        var dispatcher = CreateDispatcher(new BillingWebhookOptions());

        await dispatcher.DispatchAsync("usage.daily", new { tenantId = Guid.NewGuid() });

        dispatcher.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_WhenConfigured_QueuesSerialisedEvent()
    {
        var dispatcher = CreateDispatcher(Configured);

        await dispatcher.DispatchAsync("quota.warning", new { spend = 80m, limit = 100m });

        dispatcher.Reader.TryRead(out var queued).Should().BeTrue();
        queued!.EventType.Should().Be("quota.warning");

        using var json = JsonDocument.Parse(queued.Body);
        json.RootElement.GetProperty("type").GetString().Should().Be("quota.warning");
        json.RootElement.GetProperty("data").GetProperty("spend").GetDecimal().Should().Be(80m);
    }

    /// <summary>
    /// Callers reserve a once-per-period slot in a dedup tracker before dispatching. A delivery that
    /// can never happen has to hand that reservation back, or the event is permanently consumed.
    /// </summary>
    [Fact]
    public async Task SenderService_WhenDeliveryPermanentlyFails_InvokesFailureCallback()
    {
        var dispatcher = CreateDispatcher(Configured);
        var released = false;

        await dispatcher.DispatchAsync("quota.warning", new { ok = true }, () => released = true);
        dispatcher.CompleteQueue();

        // 400 is not retryable, so this fails on the first attempt without any backoff delay.
        await RunSenderAsync(dispatcher, new StubHttpMessageHandler(HttpStatusCode.BadRequest));

        released.Should().BeTrue();
    }

    [Fact]
    public async Task SenderService_WhenDeliverySucceeds_DoesNotInvokeFailureCallback()
    {
        var dispatcher = CreateDispatcher(Configured);
        var released = false;

        await dispatcher.DispatchAsync("usage.daily", new { ok = true }, () => released = true);
        dispatcher.CompleteQueue();

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        await RunSenderAsync(dispatcher, handler);

        released.Should().BeFalse();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task SenderService_SendsSignatureAndTimestampHeaders()
    {
        var dispatcher = CreateDispatcher(Configured);
        await dispatcher.DispatchAsync("usage.daily", new { ok = true });
        dispatcher.CompleteQueue();

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        await RunSenderAsync(dispatcher, handler);

        var request = handler.Requests.Single();
        request.Signature.Should().MatchRegex("^t=[0-9]+,v1=[0-9a-f]{64}$");
        request.Timestamp.Should().NotBeNullOrEmpty();
        request.Signature.Should().Be(
            BillingWebhookDispatcher.ComputeSignature(
                request.Body, "test-secret", DateTimeOffset.FromUnixTimeSeconds(long.Parse(request.Timestamp))));
    }

    private static BillingWebhookDispatcher CreateDispatcher(BillingWebhookOptions options) =>
        new(Options.Create(options), NullLogger<BillingWebhookDispatcher>.Instance);

    private static async Task RunSenderAsync(
        BillingWebhookDispatcher dispatcher,
        HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(BillingWebhookSenderHostedService.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var sender = new BillingWebhookSenderHostedService(
            dispatcher,
            factory,
            Options.Create(Configured),
            NullLogger<BillingWebhookSenderHostedService>.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sender.StartAsync(timeout.Token);
        await sender.ExecuteTask!.WaitAsync(timeout.Token);
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                body,
                Header(request, "X-33pol-Signature"),
                Header(request, "X-33pol-Timestamp")));

            return new HttpResponseMessage(statusCode);
        }

        private static string Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : string.Empty;
    }

    private sealed record CapturedRequest(string Uri, string Body, string Signature, string Timestamp);
}
