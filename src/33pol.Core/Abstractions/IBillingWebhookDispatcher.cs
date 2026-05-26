namespace Pol33.Core.Abstractions;

public interface IBillingWebhookDispatcher
{
    Task DispatchAsync(
        string eventType,
        object payload,
        CancellationToken cancellationToken = default);
}
