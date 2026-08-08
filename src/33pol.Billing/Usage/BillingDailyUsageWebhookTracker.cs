using System.Collections.Concurrent;

namespace Pol33.Billing.Usage;

public sealed class BillingDailyUsageWebhookTracker
{
    private readonly ConcurrentDictionary<string, byte> _sent = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _sync = new();
    private readonly int _retentionLimit;

    public BillingDailyUsageWebhookTracker(int retentionLimit = 100_000)
    {
        _retentionLimit = Math.Max(1, retentionLimit);
    }

    private static string BuildKey(Guid tenantId, DateOnly usageDate) =>
        $"{tenantId:N}:{usageDate:yyyy-MM-dd}";

    /// <summary>
    /// Reserves the once-per-tenant-day send. The reservation must be handed back with
    /// <see cref="Release"/> if delivery ultimately fails.
    /// </summary>
    public bool TryMarkSent(Guid tenantId, DateOnly usageDate)
    {
        var key = BuildKey(tenantId, usageDate);
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
    /// Hands back a reservation whose delivery permanently failed, so the day's summary can be
    /// retried on the next scheduled pass.
    /// </summary>
    public void Release(Guid tenantId, DateOnly usageDate)
    {
        lock (_sync)
        {
            _sent.TryRemove(BuildKey(tenantId, usageDate), out _);
        }
    }
}
