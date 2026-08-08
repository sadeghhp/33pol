namespace Pol33.Core.Abstractions;

public interface IBillingWebhookDispatcher
{
    /// <summary>
    /// Hands a webhook to the delivery pipeline. Returns once the event is queued, not once it has
    /// been delivered.
    /// </summary>
    /// <param name="onPermanentFailure">
    /// Invoked when delivery has failed and every retry is exhausted. Callers that guard a webhook
    /// with a once-only tracker must use this to release their reservation, otherwise a single
    /// receiver outage silently consumes the only send the event will ever get.
    /// </param>
    /// <remarks>
    /// Delivery is asynchronous by contract. It used to be awaited inline on the usage-persistence
    /// path, which meant a slow or hanging receiver stalled the single-reader billing writer, backed
    /// the usage queue up to saturation and dropped billing events — a webhook consumer's latency
    /// silently corrupting the gateway's own accounting.
    /// </remarks>
    Task DispatchAsync(
        string eventType,
        object payload,
        Action? onPermanentFailure,
        CancellationToken cancellationToken = default);

    Task DispatchAsync(
        string eventType,
        object payload,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(eventType, payload, onPermanentFailure: null, cancellationToken);
}
