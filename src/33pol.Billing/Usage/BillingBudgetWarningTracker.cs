using System.Collections.Concurrent;

namespace Pol33.Billing.Usage;

public sealed class BillingBudgetWarningTracker
{
    private readonly ConcurrentDictionary<string, byte> _sent = new(StringComparer.Ordinal);

    public bool TryMarkSent(string key) => _sent.TryAdd(key, 0);
}
