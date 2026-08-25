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
/// buffer there turns a failing backend into an out-of-memory kill. Every record that does not reach
/// the archive is counted — dropped for space, or failed after its one retry — and those counts are
/// reported on the Errors tab, so a page served from the database can say how incomplete it is.
/// </remarks>
public sealed class GatewayErrorBatchPersistenceHandler(
    IServiceScopeFactory scopeFactory,
    IOptions<GatewayErrorTrackingOptions> options,
    ILogger<GatewayErrorBatchPersistenceHandler> logger) : IGatewayErrorArchiveWriter, IHostedService
{
    private readonly object _gate = new();

    // Only one flush runs at a time. The size trigger in Enqueue takes the gate opportunistically
    // (Wait(0)); when a flush is already in progress the records simply stay in _pending — subject
    // to the MaxPending trim — until the timer loop drains them. This keeps memory bounded by
    // MaxPending: without it every size trigger during a database stall would spawn another drained
    // batch waiting behind the semaphore, growing with error rate x stall duration instead of with
    // the configured cap.
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly List<GatewayErrorRecord> _pending = [];
    private readonly GatewayErrorTrackingOptions _options = options.Value;
    private Task? _flushLoop;
    private CancellationTokenSource? _cts;
    private long _dropped;
    private long _droppedTotal;
    private long _persistFailedTotal;

    // A batch whose write failed once. Written ahead of the next drain; if it fails again it is
    // counted as lost rather than retried forever against a database that is not coming back.
    private List<GatewayErrorRecord>? _retry;

    // Bumped by DiscardPending so a batch drained before a wipe is not written after it.
    private long _generation;

    private int BatchSize => Math.Max(1, _options.WriterBatchSize);

    public long DroppedTotal => Interlocked.Read(ref _droppedTotal);

    public long PersistFailedTotal => Interlocked.Read(ref _persistFailedTotal);

    private int MaxPending => BatchSize * 10;

    /// <summary>Records currently buffered and not yet handed to a flush. Exposed for tests.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public void Enqueue(GatewayErrorRecord record)
    {
        if (record is null || !_options.Enabled)
        {
            return;
        }

        List<GatewayErrorRecord>? toFlush = null;
        var retrying = 0;
        long generation = 0;
        lock (_gate)
        {
            _pending.Add(record);

            if (_pending.Count > MaxPending)
            {
                var overflow = _pending.Count - MaxPending;
                _pending.RemoveRange(0, overflow);
                _dropped += overflow;
                Interlocked.Add(ref _droppedTotal, overflow);
            }

            if (_pending.Count >= BatchSize && _flushGate.Wait(0))
            {
                // Gate acquired: this call owns the flush and WriteBatchAndReleaseAsync releases it.
                toFlush = DrainLocked(out retrying);
                generation = _generation;
            }
        }

        if (toFlush is not null)
        {
            // Fire and forget: the caller is a request thread answering a client that has already
            // failed once. Exceptions are handled inside WriteBatchAndReleaseAsync.
            _ = WriteBatchAndReleaseAsync(toFlush, retrying, generation, CancellationToken.None);
        }
    }

    public async Task FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown deadline hit while another flush held the gate; the records are diagnostics.
            return;
        }

        List<GatewayErrorRecord>? toFlush = null;
        var retrying = 0;
        long generation;
        lock (_gate)
        {
            if (_pending.Count > 0 || _retry is not null)
            {
                toFlush = DrainLocked(out retrying);
            }

            generation = _generation;
        }

        if (toFlush is null)
        {
            _flushGate.Release();
            return;
        }

        await WriteBatchAndReleaseAsync(toFlush, retrying, generation, cancellationToken).ConfigureAwait(false);
    }

    public void DiscardPending()
    {
        lock (_gate)
        {
            _pending.Clear();
            _retry = null;
            _generation++;

            // A wipe rebases the archive; the loss counters describe the archive being wiped.
            Interlocked.Exchange(ref _droppedTotal, 0);
            Interlocked.Exchange(ref _persistFailedTotal, 0);
            _dropped = 0;
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

    /// <summary>
    /// Takes everything buffered, with the retry batch (if any) at the front. <paramref name="retrying"/>
    /// is how many leading records are on their second attempt.
    /// </summary>
    private List<GatewayErrorRecord> DrainLocked(out int retrying)
    {
        retrying = _retry?.Count ?? 0;
        var batch = new List<GatewayErrorRecord>(retrying + _pending.Count);
        if (_retry is not null)
        {
            batch.AddRange(_retry);
            _retry = null;
        }

        batch.AddRange(_pending);
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

    /// <summary>
    /// Writes one drained batch. The caller must already hold <see cref="_flushGate"/>; it is
    /// released here on every path.
    /// </summary>
    private async Task WriteBatchAndReleaseAsync(
        List<GatewayErrorRecord> batch,
        int retrying,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
            {
                if (generation != _generation)
                {
                    // DiscardPending ran after this batch was drained: the operator wiped the
                    // archive and these records must not land in it afterwards.
                    return;
                }
            }

            using var scope = scopeFactory.CreateScope();
            var archive = scope.ServiceProvider.GetRequiredService<IGatewayErrorArchive>();
            await archive.AppendBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-write. Counted as lost: the in-memory buffer dies with the process.
            Interlocked.Add(ref _persistFailedTotal, batch.Count);
        }
        catch (Exception ex)
        {
            // First failure: hold the fresh records for one more attempt on the next flush. Records
            // already on their retry are counted as lost so a dead database cannot pin them forever.
            var fresh = batch.Count - retrying;
            bool rebased;
            lock (_gate)
            {
                // A clear-all rebased the archive while this batch was in flight: these records
                // were meant to go, so they are neither retried nor counted as lost.
                rebased = generation != _generation;
                if (!rebased && fresh > 0)
                {
                    _retry = batch.GetRange(retrying, fresh);
                    if (_retry.Count > MaxPending)
                    {
                        var overflow = _retry.Count - MaxPending;
                        _retry.RemoveRange(0, overflow);
                        _dropped += overflow;
                        Interlocked.Add(ref _droppedTotal, overflow);
                    }
                }
            }

            if (!rebased)
            {
                Interlocked.Add(ref _persistFailedTotal, retrying);
            }

            logger.LogWarning(
                ex,
                "Failed to persist {Count} error records ({Lost} lost after retry, {Retrying} queued for one retry).",
                batch.Count,
                retrying,
                fresh);
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
                "The Errors tab reports the running total of dropped records.",
                dropped);
        }
    }
}
