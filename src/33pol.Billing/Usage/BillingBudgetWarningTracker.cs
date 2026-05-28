using System.Collections.Concurrent;

namespace Pol33.Billing.Usage;

public sealed class BillingBudgetWarningTracker
{
    private readonly ConcurrentDictionary<string, byte> _sent = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _sync = new();
    private readonly int _retentionLimit;

    public BillingBudgetWarningTracker(int retentionLimit = 100_000)
    {
        _retentionLimit = Math.Max(1, retentionLimit);
    }

    public bool TryMarkSent(string key)
    {
        lock (_sync)
        {
            if (!_sent.TryAdd(key, 0))
            {
                return false;
            }

            _order.Enqueue(key);
            while (_sent.Count > _retentionLimit && _order.TryDequeue(out var oldestKey))
            {
                _sent.TryRemove(oldestKey, out _);
            }

            return true;
        }
    }
}
