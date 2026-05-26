using System.Collections.Concurrent;

namespace Pol33.Billing.Usage;

public sealed class BillingDailyUsageWebhookTracker
{
    private readonly ConcurrentDictionary<string, byte> _sent = new(StringComparer.Ordinal);

    public bool TryMarkSent(Guid tenantId, DateOnly usageDate) =>
        _sent.TryAdd($"{tenantId:N}:{usageDate:yyyy-MM-dd}", 0);
}
