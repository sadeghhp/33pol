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

    /// <summary>
    /// Reserves the once-per-period send for <paramref name="key"/>. The reservation must be handed
    /// back with <see cref="Release"/> if delivery ultimately fails.
    /// </summary>
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

    /// <summary>
    /// Hands back a reservation whose delivery permanently failed, so the warning can be sent again
    /// on a later evaluation.
    /// </summary>
    /// <remarks>
    /// Without this, marking before dispatching made delivery at-most-once: a receiver that was down
    /// for one attempt consumed the only budget warning that period would ever produce.
    /// </remarks>
    public void Release(string key)
    {
        lock (_sync)
        {
            _sent.TryRemove(key, out _);
        }
    }
}
