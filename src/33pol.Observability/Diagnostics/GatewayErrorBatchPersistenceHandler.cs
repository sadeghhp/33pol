using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Observability.Diagnostics;

/// <summary>
/// Buffers error records and writes them to the archive in batches, off the request path.
/// </summary>
/// <remarks>
/// Mirrors the usage writer in the billing layer, with one deliberate difference: the pending list
/// is bounded and drops its oldest entries when full. Usage events arrive at a trickle, but an
/// error storm is precisely the moment the database is least likely to keep up — an unbounded
/// buffer there turns a failing backend into an out-of-memory kill. The in-memory group aggregates
/// still count every dropped record, so the console's totals stay honest even when detail is lost.
/// </remarks>
public sealed class GatewayErrorBatchPersistenceHandler(
    IServiceScopeFactory scopeFactory,
    IOptions<GatewayErrorTrackingOptions> options,
    ILogger<GatewayErrorBatchPersistenceHandler> logger) : IGatewayErrorArchiveWriter, IHostedService
{
    private readonly object _gate = new();

    // Serializes the size-triggered flush from the recording thread against the timer loop, so the
    // two cannot interleave writes of the same batch.
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly List<GatewayErrorRecord> _pending = [];
    private readonly GatewayErrorTrackingOptions _options = options.Value;
    private Task? _flushLoop;
    private CancellationTokenSource? _cts;
    private long _dropped;

    private int BatchSize => Math.Max(1, _options.WriterBatchSize);

    private int MaxPending => BatchSize * 10;

    public void Enqueue(GatewayErrorRecord record)
    {
        if (record is null || !_options.Enabled)
        {
            return;
        }

        List<GatewayErrorRecord>? toFlush = null;
        lock (_gate)
        {
            _pending.Add(record);

            if (_pending.Count > MaxPending)
            {
                var overflow = _pending.Count - MaxPending;
                _pending.RemoveRange(0, overflow);
                _dropped += overflow;
            }

            if (_pending.Count >= BatchSize)
            {
                toFlush = DrainLocked();
            }
        }

        if (toFlush is not null)
        {
            // Fire and forget: the caller is a request thread answering a client that has already
            // failed once. Exceptions are handled inside FlushBatchAsync.
            _ = FlushBatchAsync(toFlush, CancellationToken.None);
        }
    }

    public async Task FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        List<GatewayErrorRecord>? toFlush;
        lock (_gate)
        {
            toFlush = _pending.Count == 0 ? null : DrainLocked();
        }

        if (toFlush is not null)
        {
            await FlushBatchAsync(toFlush, cancellationToken).ConfigureAwait(false);
        }
    }

    public void DiscardPending()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
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

        await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
    }

    private List<GatewayErrorRecord> DrainLocked()
    {
        var batch = new List<GatewayErrorRecord>(_pending);
        _pending.Clear();
        return batch;
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(250, _options.WriterFlushIntervalMs));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
                ReportDrops();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown. StopAsync performs the final drain.
        }
    }

    private async Task FlushBatchAsync(List<GatewayErrorRecord> batch, CancellationToken cancellationToken)
    {
        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var archive = scope.ServiceProvider.GetRequiredService<IGatewayErrorArchive>();
            await archive.AppendBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-write. The records are diagnostics; losing them is acceptable.
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to persist {Count} error records; they remain visible in the in-memory buffer only.",
                batch.Count);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private void ReportDrops()
    {
        long dropped;
        lock (_gate)
        {
            dropped = _dropped;
            _dropped = 0;
        }

        if (dropped > 0)
        {
            logger.LogWarning(
                "Dropped {Count} error records before persistence because the write buffer was full. " +
                "Group counts in the Errors tab remain accurate; individual occurrences were lost.",
                dropped);
        }
    }
}
