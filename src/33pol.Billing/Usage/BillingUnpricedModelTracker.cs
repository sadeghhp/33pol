using System.Collections.Concurrent;

namespace Pol33.Billing.Usage;

/// <summary>
/// Tracks which models have already been reported as unpriced, so the warning is logged once per
/// model rather than once per usage event. Bounded like the other billing trackers.
/// </summary>
public sealed class BillingUnpricedModelTracker
{
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();
    private readonly object _sync = new();
    private readonly int _retentionLimit;

    public BillingUnpricedModelTracker(int retentionLimit = 10_000)
    {
        _retentionLimit = Math.Max(1, retentionLimit);
    }

    public bool TryMarkWarned(string modelId)
    {
        lock (_sync)
        {
            if (!_warned.TryAdd(modelId, 0))
            {
                return false;
            }

            _order.Enqueue(modelId);
            while (_warned.Count > _retentionLimit && _order.TryDequeue(out var oldest))
            {
                _warned.TryRemove(oldest, out _);
            }

            return true;
        }
    }

    /// <summary>Called when a model gains a price, so a later regression is reported again.</summary>
    public void Clear(string modelId)
    {
        lock (_sync)
        {
            _warned.TryRemove(modelId, out _);
        }
    }
}
