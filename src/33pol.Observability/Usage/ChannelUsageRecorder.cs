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
    private readonly Channel<UsageEvent> _channel = Channel.CreateBounded<UsageEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly IQuotaService _quotaService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChannelUsageRecorder> _logger;
    private Task? _worker;

    public ChannelUsageRecorder(
        IQuotaService quotaService,
        IServiceScopeFactory scopeFactory,
        ILogger<ChannelUsageRecorder> logger)
    {
        _quotaService = quotaService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Enqueue(UsageEvent usageEvent)
    {
        if (!_channel.Writer.TryWrite(usageEvent))
        {
            _logger.LogWarning("Usage event dropped for request {RequestId}", usageEvent.RequestId);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => ProcessAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_worker is not null)
        {
            await _worker.ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await foreach (var usage in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var totalTokens = usage.PromptTokens + usage.CompletionTokens;
            var partition = usage.TenantId ?? "anonymous";
            _quotaService.CommitUsage(partition, usage.ModelId, totalTokens, usage.RequestId);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var persistence = scope.ServiceProvider.GetRequiredService<IUsagePersistenceHandler>();
            await persistence.PersistAsync(usage, cancellationToken).ConfigureAwait(false);

            GatewayMeters.TokensTotal.Add(
                totalTokens,
                new KeyValuePair<string, object?>("model", usage.ModelId),
                new KeyValuePair<string, object?>("direction", "total"));
        }
    }
}
