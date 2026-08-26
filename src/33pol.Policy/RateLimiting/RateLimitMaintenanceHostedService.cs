using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Policy.RateLimiting;

/// <summary>
/// Runs the two pieces of rate-limiting work that must not happen on a request thread: sweeping the
/// partition tables, and recomputing the adaptive factors.
/// </summary>
/// <remarks>
/// <para>Both used to be, or would naturally have been, inline. Sweeping ran on whichever request
/// tripped an operation counter, so one request in a few hundred paid an O(live partitions) scan and
/// — past the ceiling — a copy and sort of the whole table. Evaluating adaptation inline would be
/// worse still: it reads every model's bulkhead and breaker state, work whose cost has nothing to do
/// with the request that happened to trigger it. On a timer both cost the same total work at a rate
/// set by wall-clock rather than by traffic, and no client waits for either.</para>
///
/// <para>Adaptation is evaluated every tick and sweeping every tick as well; a sweep of an idle
/// table is a cheap enumeration, and the retention window is what actually decides whether anything
/// is removed.</para>
/// </remarks>
public sealed class RateLimitMaintenanceHostedService : BackgroundService
{
    private readonly IDistributedRateLimitStore _store;
    private readonly IAdaptiveRateLimitGovernor _governor;
    private readonly ILogger<RateLimitMaintenanceHostedService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;

    public RateLimitMaintenanceHostedService(
        IDistributedRateLimitStore store,
        IAdaptiveRateLimitGovernor governor,
        ILogger<RateLimitMaintenanceHostedService> logger,
        IOptions<RateLimitingOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _governor = governor;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _interval = TimeSpan.FromSeconds(Math.Clamp(options?.Value.MaintenanceIntervalSeconds ?? 10, 1, 300));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            RunOnce();
        }
    }

    /// <summary>One maintenance pass. Internal so a test can drive it without a host or a clock.</summary>
    internal void RunOnce()
    {
        var now = _timeProvider.GetUtcNow();

        try
        {
            var removed = _store.Compact(now);
            if (removed > 0)
            {
                _logger.LogDebug("Rate-limit maintenance removed {Removed} idle partition(s).", removed);
            }
        }
        catch (Exception ex)
        {
            // A failed sweep must not take the loop down with it: the next tick would then never
            // run, the tables would grow unbounded, and the only symptom would be memory.
            _logger.LogError(ex, "Rate-limit partition sweep failed.");
        }

        try
        {
            _governor.Evaluate(now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adaptive rate-limit evaluation failed; limits stay as last computed.");
        }
    }
}
