namespace Pol33.Registry.Services;

/// <summary>
/// Serializes registry file reloads and writer mutations.
/// </summary>
public sealed class RegistryGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public bool IsHeld => _mutex.CurrentCount == 0;

    public async Task<bool> TryEnterAsync(CancellationToken cancellationToken = default) =>
        await _mutex.WaitAsync(0, cancellationToken).ConfigureAwait(false);

    public async Task EnterAsync(CancellationToken cancellationToken = default) =>
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);

    public void Release() => _mutex.Release();
}
