using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Webhooks;

/// <summary>
/// Delivers queued billing webhooks with bounded retry, off the usage-persistence path.
/// </summary>
/// <remarks>
/// <para>Delivery used to be a single inline attempt whose failure was logged and discarded. Because
/// every call site reserves a once-per-period slot in a dedup tracker <em>before</em> dispatching, a
/// receiver returning 503 once meant that budget warning or daily summary was never sent again —
/// at-most-once delivery for the signals operators rely on to catch runaway spend.</para>
///
/// <para>Retries are attempted only for failures that can plausibly succeed later (transport errors,
/// timeouts, 5xx, 408, 429). A 4xx that indicates the receiver rejected the event is permanent, so
/// retrying it only delays the failure callback.</para>
/// </remarks>
public sealed class BillingWebhookSenderHostedService(
    BillingWebhookDispatcher dispatcher,
    IHttpClientFactory httpClientFactory,
    IOptions<BillingWebhookOptions> options,
    ILogger<BillingWebhookSenderHostedService> logger) : BackgroundService
{
    public const string HttpClientName = nameof(BillingWebhookDispatcher);

    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var delivery in dispatcher.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await DeliverAsync(delivery, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One malformed delivery must not stop the loop and silence every later webhook.
                logger.LogError(ex, "Webhook {EventType} delivery loop error", delivery.EventType);
                BillingWebhookDispatcher.SafeInvoke(delivery.OnPermanentFailure, delivery.EventType, logger);
            }
        }
    }

    private async Task DeliverAsync(BillingWebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var webhook = options.Value;
        if (!webhook.IsConfigured)
        {
            return;
        }

        for (var attempt = 0; attempt <= RetryBackoff.Length; attempt++)
        {
            var (delivered, retryable, detail) = await TrySendAsync(delivery, webhook, cancellationToken)
                .ConfigureAwait(false);

            if (delivered)
            {
                if (attempt > 0)
                {
                    logger.LogInformation(
                        "Webhook {EventType} delivered on attempt {Attempt}", delivery.EventType, attempt + 1);
                }

                return;
            }

            if (!retryable || attempt == RetryBackoff.Length)
            {
                logger.LogError(
                    "Webhook {EventType} permanently failed after {Attempts} attempt(s): {Detail}",
                    delivery.EventType,
                    attempt + 1,
                    detail);
                BillingWebhookDispatcher.SafeInvoke(delivery.OnPermanentFailure, delivery.EventType, logger);
                return;
            }

            logger.LogWarning(
                "Webhook {EventType} attempt {Attempt} failed ({Detail}); retrying in {DelaySeconds}s",
                delivery.EventType,
                attempt + 1,
                detail,
                RetryBackoff[attempt].TotalSeconds);

            await Task.Delay(RetryBackoff[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(bool Delivered, bool Retryable, string Detail)> TrySendAsync(
        BillingWebhookDelivery delivery,
        BillingWebhookOptions webhook,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var signature = BillingWebhookDispatcher.ComputeSignature(delivery.Body, webhook.Secret, timestamp);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, webhook.EndpointUrl)
            {
                Content = new StringContent(delivery.Body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-33pol-Signature", signature);
            request.Headers.Add("X-33pol-Timestamp", timestamp.ToUnixTimeSeconds().ToString());
            request.Headers.Add("X-33pol-Event", delivery.EventType);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (true, false, "ok")
                : (false, IsRetryableStatus(response.StatusCode), $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (false, true, ex.GetType().Name);
        }
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        (int)status >= 500 ||
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        dispatcher.CompleteQueue();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
