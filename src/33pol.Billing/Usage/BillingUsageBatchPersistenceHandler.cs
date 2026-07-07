using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

public sealed class BillingUsageBatchPersistenceHandler(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingOptions> options,
    ILogger<BillingUsageBatchPersistenceHandler> logger) : IUsagePersistenceHandler, IHostedService
{
    private readonly object _gate = new();
    // Serializes the two flush paths (size-triggered from PersistAsync on the channel-reader thread,
    // and the periodic timer loop) so they cannot run overlapping read-modify-write updates against
    // the same daily rollup row and lose one another's increments.
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly List<UsageEvent> _pending = [];
    private Task? _flushLoop;
    private CancellationTokenSource? _cts;

    public ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        List<UsageEvent>? toFlush = null;
        lock (_gate)
        {
            _pending.Add(usageEvent);
            if (_pending.Count >= options.Value.UsageWriterBatchSize)
            {
                toFlush = DrainLocked();
            }
        }

        return toFlush is null
            ? ValueTask.CompletedTask
            : FlushBatchAsync(toFlush, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _flushLoop = RunFlushLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_flushLoop is not null)
        {
            await _flushLoop.ConfigureAwait(false);
        }

        List<UsageEvent> remaining;
        lock (_gate)
        {
            remaining = DrainLocked();
        }

        if (remaining.Count > 0)
        {
            await FlushBatchAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.Value.UsageWriterFlushIntervalMs);
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                List<UsageEvent>? batch;
                lock (_gate)
                {
                    batch = _pending.Count > 0 ? DrainLocked() : null;
                }

                if (batch is not null)
                {
                    await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private List<UsageEvent> DrainLocked()
    {
        var batch = new List<UsageEvent>(_pending);
        _pending.Clear();
        return batch;
    }

    private async ValueTask FlushBatchAsync(IReadOnlyList<UsageEvent> batch, CancellationToken cancellationToken)
    {
        try
        {
            await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Flush of {Count} usage events skipped due to shutdown", batch.Count);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<BillingUsagePersistenceHandler>();
            await handler.PersistBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist {Count} usage events", batch.Count);
        }
        finally
        {
            _flushGate.Release();
        }
    }
}
