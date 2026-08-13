namespace Pol33.App.DependencyInjection;

/// <summary>
/// Serializes writes to the persisted counter snapshot against operations that reset the in-memory
/// counters.
/// </summary>
/// <remarks>
/// The snapshot service exports the counters and then saves them, two steps with a database round
/// trip between. Without this gate, a flush that read the counters <em>before</em> a clear could
/// save them <em>after</em> it, restoring every error total the operator just cleared — and the
/// clear would appear to work until the next restart, which is the worst possible time to discover
/// it did not.
/// </remarks>
internal sealed class GatewayStatsFlushCoordinator : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Release(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
