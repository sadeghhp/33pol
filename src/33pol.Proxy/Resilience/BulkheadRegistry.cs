using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Proxy.Resilience;

public sealed class BulkheadRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.Ordinal);
    private readonly int _maxConcurrent;
    private readonly IGatewayMetricsCollector _metrics;

    public BulkheadRegistry(IOptions<GatewayOptions> options, IGatewayMetricsCollector metrics)
    {
        _maxConcurrent = options.Value.Resilience.MaxConcurrentForwardsPerModel;
        _metrics = metrics;
    }

    public async ValueTask<IDisposable?> TryAcquireAsync(string modelId, CancellationToken cancellationToken)
    {
        var semaphore = _semaphores.GetOrAdd(
            modelId,
            static (id, max) => new SemaphoreSlim(max, max),
            _maxConcurrent);

        if (!await semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _metrics.RecordBulkheadRejection(modelId);
            return null;
        }

        _metrics.RecordBulkheadInflightChange(modelId, 1);
        return new ReleaseHandle(semaphore, modelId, _metrics);
    }

    private sealed class ReleaseHandle(
        SemaphoreSlim semaphore,
        string modelId,
        IGatewayMetricsCollector metrics) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
                metrics.RecordBulkheadInflightChange(modelId, -1);
            }
        }
    }
}
