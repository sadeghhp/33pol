using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;

namespace Pol33.Proxy.Resilience;

public sealed class BulkheadRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.Ordinal);
    private readonly int _maxConcurrent;

    public BulkheadRegistry(IOptions<GatewayOptions> options)
    {
        _maxConcurrent = options.Value.Resilience.MaxConcurrentForwardsPerModel;
    }

    public async ValueTask<IDisposable?> TryAcquireAsync(string modelId, CancellationToken cancellationToken)
    {
        var semaphore = _semaphores.GetOrAdd(
            modelId,
            static (id, max) => new SemaphoreSlim(max, max),
            _maxConcurrent);

        if (!await semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReleaseHandle(semaphore);
    }

    private sealed class ReleaseHandle(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
