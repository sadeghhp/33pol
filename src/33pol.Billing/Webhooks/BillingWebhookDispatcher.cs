using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Billing.Webhooks;

/// <summary>A queued webhook awaiting delivery by <see cref="BillingWebhookSenderHostedService"/>.</summary>
internal sealed record BillingWebhookDelivery(
    string EventType,
    string Body,
    Action? OnPermanentFailure);

/// <summary>
/// Serialises and queues billing webhooks. Delivery itself happens on
/// <see cref="BillingWebhookSenderHostedService"/>, off the usage-persistence path.
/// </summary>
public sealed class BillingWebhookDispatcher(
    IOptions<BillingWebhookOptions> options,
    ILogger<BillingWebhookDispatcher> logger) : IBillingWebhookDispatcher
{
    /// <summary>
    /// Bounded so a wedged receiver cannot grow the queue without limit. Small, because the events
    /// are low-rate (budget warnings and one daily summary per tenant) — reaching this depth means
    /// delivery is broken, and the drop is logged rather than silent.
    /// </summary>
    private const int QueueCapacity = 1024;

    private readonly Channel<BillingWebhookDelivery> _queue =
        Channel.CreateBounded<BillingWebhookDelivery>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal ChannelReader<BillingWebhookDelivery> Reader => _queue.Reader;

    internal void CompleteQueue() => _queue.Writer.TryComplete();

    public Task DispatchAsync(
        string eventType,
        object payload,
        Action? onPermanentFailure,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsConfigured)
        {
            return Task.CompletedTask;
        }

        var body = JsonSerializer.Serialize(
            new { type = eventType, timestamp = DateTimeOffset.UtcNow, data = payload },
            JsonOptions);

        if (!_queue.Writer.TryWrite(new BillingWebhookDelivery(eventType, body, onPermanentFailure)))
        {
            // Saturated: delivery is already failing. Release the caller's once-only reservation so
            // the event is not permanently consumed by a queue it never entered.
            logger.LogError(
                "Webhook {EventType} dropped: delivery queue is full ({Capacity}). "
                + "The configured receiver is not keeping up or is unreachable.",
                eventType,
                QueueCapacity);
            SafeInvoke(onPermanentFailure, eventType, logger);
        }

        return Task.CompletedTask;
    }

    public Task DispatchAsync(string eventType, object payload, CancellationToken cancellationToken = default) =>
        DispatchAsync(eventType, payload, onPermanentFailure: null, cancellationToken);

    internal static void SafeInvoke(Action? callback, string eventType, ILogger logger)
    {
        if (callback is null)
        {
            return;
        }

        try
        {
            callback();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Webhook {EventType} failure callback threw", eventType);
        }
    }

    /// <summary>
    /// Computes the signature carried in <c>X-33pol-Signature</c>.
    /// </summary>
    /// <remarks>
    /// The timestamp is part of the signed material, in the <c>t=&lt;unix&gt;,v1=&lt;hmac&gt;</c>
    /// form used by other webhook producers. Signing the body alone made every delivery replayable
    /// forever: a receiver that verified the signature had no way to tell a fresh event from a
    /// captured one. Receivers should reject deliveries whose <c>t</c> is outside their tolerance.
    /// </remarks>
    public static string ComputeSignature(string body, string secret, DateTimeOffset timestamp)
    {
        var unix = timestamp.ToUnixTimeSeconds();
        var signedPayload = $"{unix}.{body}";
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signedPayload));
        return $"t={unix},v1={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
