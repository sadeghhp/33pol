using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Usage;

public sealed class ChannelUsageRecorder : IUsageRecorder, IHostedService, IUsageWriterStateSource
{
    private const int ChannelCapacity = 10_000;

    private readonly Channel<UsageEvent> _channel = Channel.CreateBounded<UsageEvent>(
        new BoundedChannelOptions(ChannelCapacity)
        {
            // Wait (not DropOldest): a full channel makes TryWrite report failure so the drop is
            // explicit and metered, instead of silently evicting the oldest unpersisted billing event.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Upper bound on the final flush at shutdown, independent of the host's token. Mirrors the
    /// deadline the batch handler gives its own last flush.
    /// </summary>
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IQuotaService _quotaService;
    private readonly IUsagePersistenceHandler _persistence;
    private readonly IGatewayMetricsCollector _metricsCollector;
    private readonly ILogger<ChannelUsageRecorder> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private int _stopped;

    /// <remarks>
    /// Takes <see cref="IUsagePersistenceHandler"/> itself rather than an
    /// <see cref="IServiceScopeFactory"/> to resolve it from. Every registration of that handler is
    /// a singleton, so the scope this used to open per event resolved the same root object and
    /// disposed nothing — but it made the writer depend on the container still being alive. The
    /// container's lifetime belongs to the host, not to this service, and the one place that
    /// mattered was <see cref="StopAsync"/>: the final flush ran against a provider that could
    /// already be disposed, and the billing events it was supposed to write were lost.
    /// </remarks>
    public ChannelUsageRecorder(
        IQuotaService quotaService,
        IUsagePersistenceHandler persistence,
        IGatewayMetricsCollector metricsCollector,
        ILogger<ChannelUsageRecorder> logger)
    {
        _quotaService = quotaService;
        _persistence = persistence;
        _metricsCollector = metricsCollector;
        _logger = logger;
    }

    /// <inheritdoc />
    public int QueueDepth => _channel.Reader.CanCount ? _channel.Reader.Count : -1;

    /// <inheritdoc />
    public int Capacity => ChannelCapacity;

    public bool Enqueue(UsageEvent usageEvent)
    {
        if (_channel.Writer.TryWrite(usageEvent))
        {
            GatewayMeters.UsageWriterQueueDepth.Add(1);

            // Only after the event is accepted: a dropped event is never billed, so counting its
            // tokens in gateway_tokens_total would make Prometheus diverge from usage exactly
            // during overload.
            _metricsCollector.RecordTokenUsage(
                usageEvent.ModelId,
                usageEvent.PromptTokens,
                usageEvent.CompletionTokens);
            return true;
        }

        // Channel saturated: in Wait mode TryWrite reports failure rather than evicting, so this drop
        // is accurate and counted (the previous DropOldest path lost the oldest event silently).
        // Reporting it to the caller matters as much as counting it: the router settles the
        // request's budget reservation only when persistence will actually run.
        _metricsCollector.RecordUsageEventsDropped(1);
        _logger.LogWarning("Usage event dropped (queue saturated) for request {RequestId}", usageEvent.RequestId);
        return false;
    }

    /// <remarks>
    /// The worker runs on its own cancellation source rather than the token handed to
    /// <see cref="StartAsync"/>. That token signals the host's <em>startup</em> deadline: when a
    /// startup timeout is configured it is cancelled once startup completes, which would tear the
    /// drain loop down immediately and silently stop all usage persistence for the process lifetime.
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => ProcessAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <remarks>
    /// Stops once. A hosted service can be stopped more than once by the composition around it —
    /// the test host does exactly that when a factory is disposed — and a second pass through the
    /// body below would re-run the drain and dispose <c>_stopping</c> twice.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        // Complete the writer first and let the loop drain what is already queued, so a graceful
        // shutdown does not discard billing events that were accepted from clients.
        _channel.Writer.TryComplete();

        if (_worker is not null)
        {
            var drained = await Task.WhenAny(_worker, Task.Delay(Timeout.Infinite, cancellationToken))
                .ConfigureAwait(false);
            if (drained != _worker)
            {
                // Shutdown deadline hit before the queue drained; stop the loop rather than block.
                await _stopping.CancelAsync().ConfigureAwait(false);
            }
        }

        // The batch persistence handler has already been stopped by now (hosted services stop in
        // reverse registration order), so the events the drain above just delivered are sitting in
        // its buffer with no flush loop left. Flushing here — after the drain, from the drain's own
        // consumer — is what actually gets the final partial batch to disk.
        try
        {
            // Not on cancellationToken: by the time a hosted service is stopped that token is
            // routinely already tripped — it is often what started the shutdown — and honouring it
            // for the last write would discard accepted billing events instead of persisting them.
            // The flush gets a short deadline of its own so shutdown stays bounded either way.
            using var flushDeadline = new CancellationTokenSource(ShutdownFlushTimeout);
            await _persistence.FlushPendingAsync(flushDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush pending usage events during shutdown");
        }

        _stopping.Dispose();
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await foreach (var usage in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            GatewayMeters.UsageWriterQueueDepth.Add(-1);

            try
            {
                var totalTokens = usage.PromptTokens + usage.CompletionTokens;

                // Commit to the partition the admission check reads: the stamped partition when the
                // router provided one, else the tenant id (identical for authenticated traffic).
                // The old literal-"anonymous" fallback was a bucket no check ever consulted, which
                // exempted keyless callers of public models from the monthly quota entirely; it
                // remains only as the last resort for events with no partition information at all.
                var partition = usage.QuotaPartition ?? usage.TenantId ?? "anonymous";
                _quotaService.CommitUsage(
                    partition, usage.ModelId, totalTokens, usage.RequestId, usage.TimestampUtc);

                await _persistence.PersistAsync(usage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // shutdown requested: stop draining
            }
            catch (Exception ex)
            {
                // A single failing event must not tear down the writer loop (which would silently stop
                // all usage persistence until process restart). Log and continue to the next event.
                // Named with the scope key so the admin log sink attributes the line to the request.
                _logger.LogError(ex, "Failed to persist usage event for request {GatewayRequestId}", usage.RequestId);
            }
        }
    }
}
