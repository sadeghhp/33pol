using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.Observability;

namespace Pol33.Observability.Metrics;

/// <summary>
/// Bounds how many distinct callers the request, error, duration, token and rate-limit decision
/// series may name.
/// </summary>
/// <remarks>
/// <para>A <c>tenant</c> and <c>key</c> label multiplies every series it sits on by the number of
/// callers, and a series, once exported, lives for the life of the process. So the first
/// <see cref="MaxCallers"/> callers seen get series of their own and nobody is ever evicted: a caller
/// that arrives after the budget is spent is counted under <c>key="other"</c> — still under its own
/// tenant when that tenant is already known, so a tenant-level alert keeps adding up.</para>
///
/// <para>The ceiling is <see cref="RateLimitingOptions.UsageReportMaxKeys"/>, the bound the usage
/// report already holds its keys to: one number decides how many callers the gateway will tell
/// apart, in the console and in Prometheus alike.</para>
/// </remarks>
public sealed class MetricCallerBudget
{
    private readonly ConcurrentDictionary<MetricCaller, MetricCaller> _admitted = new();
    private readonly ConcurrentDictionary<string, MetricCaller> _tenantOverflow = new(StringComparer.Ordinal);

    // Kept beside the dictionary rather than read from it: ConcurrentDictionary.Count takes every
    // bucket lock, and once the budget is spent every unknown caller would pay for it on a miss.
    private int _count;

    public MetricCallerBudget(IOptions<RateLimitingOptions>? options = null)
        : this(options?.Value.UsageReportMaxKeys ?? 500)
    {
    }

    public MetricCallerBudget(int maxCallers)
    {
        MaxCallers = Math.Clamp(maxCallers, 10, 20_000);
    }

    public int MaxCallers { get; }

    /// <summary>Callers that have a series of their own.</summary>
    public int TrackedCallers => Volatile.Read(ref _count);

    /// <summary>
    /// The labels to record <paramref name="caller"/> under. Called on the request path: one
    /// lock-free lookup for a caller already seen, and no allocation.
    /// </summary>
    public MetricCaller Resolve(MetricCaller caller)
    {
        if (!caller.IsSpecified)
        {
            return MetricCaller.Anonymous;
        }

        if (_admitted.TryGetValue(caller, out var known))
        {
            return known;
        }

        if (Interlocked.Increment(ref _count) <= MaxCallers)
        {
            if (_admitted.TryAdd(caller, caller))
            {
                _tenantOverflow.TryAdd(caller.Tenant, new MetricCaller(caller.Tenant, MetricCaller.Overflow));
                return caller;
            }

            // Lost a race to add the same caller: it is tracked, and the slot was not used.
            Interlocked.Decrement(ref _count);
            return caller;
        }

        Interlocked.Decrement(ref _count);
        return _tenantOverflow.TryGetValue(caller.Tenant, out var overflow) ? overflow : MetricCaller.Other;
    }
}
