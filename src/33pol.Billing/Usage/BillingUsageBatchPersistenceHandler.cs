using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Billing.Usage;

/// <summary>
/// Buffers usage events and writes them to the billing ledger in batches, on a size trigger and on
/// a timer.
/// </summary>
/// <remarks>
/// <para>A batch that fails to persist is <em>not</em> discarded. It is put back at the head of the
/// buffer and retried on the next tick (with a growing back-off so an outage is not hammered), up
/// to <see cref="BillingOptions.UsageWriterMaxFlushRetries"/> attempts; only then is it dropped,
/// loudly, and counted via <see cref="IGatewayMetricsCollector.RecordUsageEventsDropped"/>. The
/// ledger is the source of billing truth and reconciliation compares ledger against rollups — both
/// of which miss a dropped event — so silently losing a batch on a transient SQLite <c>busy</c> was
/// invisible everywhere. The buffer is bounded by
/// <see cref="BillingOptions.UsageWriterMaxPendingEvents"/>: past that the oldest events are shed
/// (and counted) so a long outage cannot grow memory without limit.</para>
///
/// <para>At shutdown, the final flush runs under its own short deadline
/// (<see cref="ShutdownFlushTimeout"/>) rather than the host's already-tripped token, so the last
/// second of usage is written instead of being logged as "skipped".</para>
/// </remarks>
public sealed class BillingUsageBatchPersistenceHandler(
    IServiceScopeFactory scopeFactory,
    IOptions<BillingOptions> options,
    ILogger<BillingUsageBatchPersistenceHandler> logger,
    // Optional so the handler composes without the observability layer (unit tests, tools).
    IGatewayMetricsCollector? metrics = null,
    Func<DateTimeOffset>? clock = null) : IUsagePersistenceHandler, IHostedService
{
    /// <summary>Upper bound on the final flush at shutdown, independent of the host's token.</summary>
    public static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Longest pause between retries of a failed batch.</summary>
    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    // Serializes the two flush paths (size-triggered from PersistAsync on the channel-reader thread,
    // and the periodic timer loop) so they cannot run overlapping read-modify-write updates against
    // the same daily rollup row and lose one another's increments.
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly List<UsageEvent> _pending = [];
    private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    private Task? _flushLoop;
    private CancellationTokenSource? _cts;

    // Retry state for the events at the head of the buffer. Guarded by _gate.
    private int _failedAttempts;
    private DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue;

    /// <summary>Events dropped so far (retries exhausted or buffer cap hit). Exposed for tests/diagnostics.</summary>
    public long DroppedEventCount { get; private set; }

    public ValueTask PersistAsync(UsageEvent usageEvent, CancellationToken cancellationToken = default)
    {
        List<UsageEvent>? toFlush = null;
        lock (_gate)
        {
            _pending.Add(usageEvent);
            TrimToCapacityLocked();
            if (_pending.Count >= options.Value.UsageWriterBatchSize && _clock() >= _retryNotBefore)
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

        // The host's token may already be tripped by the time this runs (it is what stopped the
        // loop). Honouring it here would discard the last events instead of writing them, so the
        // final flush gets a short deadline of its own.
        using var shutdownFlush = new CancellationTokenSource(ShutdownFlushTimeout);
        await FlushPendingAsync(shutdownFlush.Token).ConfigureAwait(false);
    }

    /// <remarks>
    /// Called both from <see cref="StopAsync"/> and by the usage recorder after its shutdown drain.
    /// This instance stops before the recorder does (hosted services stop in reverse registration
    /// order), so the recorder's final events arrive with the flush loop already gone — this method
    /// is the only thing that still writes them. Draining under the gate keeps the two callers from
    /// flushing the same events twice. Retry back-off is ignored: this is the last chance.
    /// </remarks>
    public async ValueTask FlushPendingAsync(CancellationToken cancellationToken = default)
    {
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
        try
        {
            var interval = TimeSpan.FromMilliseconds(options.Value.UsageWriterFlushIntervalMs);
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                List<UsageEvent>? batch;
                lock (_gate)
                {
                    batch = _pending.Count > 0 && _clock() >= _retryNotBefore ? DrainLocked() : null;
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
        catch (Exception ex)
        {
            // Never let the loop die silently: StartAsync does not observe this task, so without
            // this the fault surfaced only at StopAsync while no timer flush ran in between.
            logger.LogCritical(
                ex,
                "The usage flush loop terminated unexpectedly. Buffered usage events will now only be "
                + "written when a batch fills ({BatchSize} events) or at shutdown.",
                options.Value.UsageWriterBatchSize);
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
            // Not a failure of the batch: put it back so the final flush (or the next caller) writes it.
            Requeue(batch);
            logger.LogDebug("Flush of {Count} usage events deferred: cancelled while waiting for the flush gate", batch.Count);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<BillingUsagePersistenceHandler>();
            await handler.PersistBatchAsync(batch, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _failedAttempts = 0;
                _retryNotBefore = DateTimeOffset.MinValue;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Requeue(batch);
            logger.LogDebug("Flush of {Count} usage events interrupted by cancellation; re-queued", batch.Count);
        }
        catch (Exception ex)
        {
            HandlePersistFailure(batch, ex);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void HandlePersistFailure(IReadOnlyList<UsageEvent> batch, Exception ex)
    {
        int attempts;
        var maxRetries = Math.Max(0, options.Value.UsageWriterMaxFlushRetries);
        lock (_gate)
        {
            attempts = ++_failedAttempts;
            if (attempts > maxRetries)
            {
                _failedAttempts = 0;
                _retryNotBefore = DateTimeOffset.MinValue;
                DroppedEventCount += batch.Count;
            }
            else
            {
                _retryNotBefore = _clock() + RetryBackoff(attempts);
            }
        }

        if (attempts > maxRetries)
        {
            metrics?.RecordUsageEventsDropped(batch.Count);
            logger.LogError(
                ex,
                "Dropping {Count} usage events after {Attempts} failed persist attempt(s). These requests "
                + "are NOT in the billing ledger and will not be billed or reconciled.",
                batch.Count,
                attempts);
            return;
        }

        Requeue(batch);
        logger.LogWarning(
            ex,
            "Failed to persist {Count} usage events (attempt {Attempt} of {MaxAttempts}); re-queued for retry.",
            batch.Count,
            attempts,
            maxRetries + 1);
    }

    private static TimeSpan RetryBackoff(int attempts)
    {
        // 1s, 2s, 4s, ... capped. Sized in whole seconds so a sub-second flush interval does not
        // retry an outage many times per second.
        var seconds = Math.Min(MaxRetryBackoff.TotalSeconds, Math.Pow(2, Math.Max(0, attempts - 1)));
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Puts a batch back at the head of the buffer, preserving order relative to events that
    /// arrived while it was being flushed. The buffer cap is applied afterwards.
    /// </summary>
    private void Requeue(IReadOnlyList<UsageEvent> batch)
    {
        lock (_gate)
        {
            _pending.InsertRange(0, batch);
            TrimToCapacityLocked();
        }
    }

    private void TrimToCapacityLocked()
    {
        var capacity = Math.Max(options.Value.UsageWriterBatchSize, options.Value.UsageWriterMaxPendingEvents);
        var overflow = _pending.Count - capacity;
        if (overflow <= 0)
        {
            return;
        }

        _pending.RemoveRange(0, overflow);
        DroppedEventCount += overflow;
        metrics?.RecordUsageEventsDropped(overflow);
        logger.LogError(
            "Usage buffer exceeded {Capacity} events while persistence is failing; shed the oldest {Count} "
            + "event(s). These requests are NOT in the billing ledger.",
            capacity,
            overflow);
    }
}
