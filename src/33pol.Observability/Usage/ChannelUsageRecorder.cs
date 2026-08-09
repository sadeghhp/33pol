using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Observability.Metrics;
using Pol33.Observability.Runtime;

namespace Pol33.Observability.Usage;

public sealed class ChannelUsageRecorder : IUsageRecorder, IHostedService
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

    private readonly IQuotaService _quotaService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IGatewayMetricsCollector _metricsCollector;
    private readonly ILogger<ChannelUsageRecorder> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public ChannelUsageRecorder(
        IQuotaService quotaService,
        IServiceScopeFactory scopeFactory,
        IGatewayMetricsCollector metricsCollector,
        ILogger<ChannelUsageRecorder> logger)
    {
        _quotaService = quotaService;
        _scopeFactory = scopeFactory;
        _metricsCollector = metricsCollector;
        _logger = logger;
    }

    public bool Enqueue(UsageEvent usageEvent)
    {
        _metricsCollector.RecordTokenUsage(
            usageEvent.ModelId,
            usageEvent.PromptTokens,
            usageEvent.CompletionTokens);

        if (_channel.Writer.TryWrite(usageEvent))
        {
            GatewayMeters.UsageWriterQueueDepth.Add(1);
            return true;
        }

        // Channel saturated: in Wait mode TryWrite reports failure rather than evicting, so this drop
        // is accurate and counted (the previous DropOldest path lost the oldest event silently).
        // Reporting it to the caller matters as much as counting it: the router settles the
        // request's budget reservation only when persistence will actually run.
        GatewayMeters.UsageWriterDropped.Add(1);
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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
            await using var scope = _scopeFactory.CreateAsyncScope();
            var persistence = scope.ServiceProvider.GetRequiredService<IUsagePersistenceHandler>();
            await persistence.FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

                await using var scope = _scopeFactory.CreateAsyncScope();
                var persistence = scope.ServiceProvider.GetRequiredService<IUsagePersistenceHandler>();
                await persistence.PersistAsync(usage, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // shutdown requested: stop draining
            }
            catch (Exception ex)
            {
                // A single failing event must not tear down the writer loop (which would silently stop
                // all usage persistence until process restart). Log and continue to the next event.
                _logger.LogError(ex, "Failed to persist usage event for request {RequestId}", usage.RequestId);
            }
        }
    }
}
