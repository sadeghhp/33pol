namespace Pol33.Registry.Services;

/// <summary>
/// Serializes registry file reloads and writer mutations.
/// </summary>
public sealed class RegistryGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool IsHeld => _semaphore.CurrentCount == 0;

    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool TryEnter() => _semaphore.Wait(0);

    public void Release() => _semaphore.Release();
}
