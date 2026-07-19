using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.App.DependencyInjection;

/// <summary>
/// Keeps the in-memory <see cref="GatewayConfigState"/> in sync with the database-backed
/// configuration. On startup it loads the first snapshot (retrying with bounded backoff until it
/// succeeds); thereafter a reconcile poll reloads only when the config version changed, and
/// <see cref="RefreshNowAsync"/> forces an immediate reload after an admin write.
///
/// <para>Fail-static: a load that throws (unreachable/corrupt database) keeps the last-good snapshot
/// and is logged — a failed load never replaces good configuration. The guard is on the thrown
/// exception, never on snapshot content, because empty config (e.g. no CORS origins) is a valid
/// configured state. Registered only when a database connection string is configured.</para>
/// </summary>
internal sealed class GatewayConfigSnapshotService(
    IServiceScopeFactory scopeFactory,
    GatewayConfigState state,
    IOptions<GatewayConfigSnapshotOptions> options,
    ILogger<GatewayConfigSnapshotService> logger) : IHostedService, IGatewayConfigRefresher
{
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private Task? _reconcileLoop;
    private CancellationTokenSource? _cts;

    /// <summary>True once the first snapshot has loaded successfully; latches on and never off.</summary>
    public bool HasLoadedOnce { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadWithRetryAsync(cancellationToken).ConfigureAwait(false);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _reconcileLoop = RunReconcileLoopAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_reconcileLoop is not null)
        {
            try
            {
                await _reconcileLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    public Task RefreshNowAsync(CancellationToken cancellationToken = default)
        => ReloadAsync(cancellationToken);

    private async Task LoadWithRetryAsync(CancellationToken cancellationToken)
    {
        var maxBackoff = TimeSpan.FromSeconds(Math.Max(1, options.Value.InitialLoadMaxBackoffSeconds));
        var delay = TimeSpan.FromMilliseconds(200);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (await TryLoadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // Bounded exponential backoff with jitter so a database that is briefly unavailable at
            // boot does not hammer it, and replicas do not retry in lockstep.
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
            await Task.Delay(delay + jitter, cancellationToken).ConfigureAwait(false);
            delay = delay < maxBackoff ? delay * 2 : maxBackoff;
            if (delay > maxBackoff)
            {
                delay = maxBackoff;
            }
        }
    }

    private async Task RunReconcileLoopAsync(CancellationToken cancellationToken)
    {
        var seconds = Math.Max(1, options.Value.ReloadIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IGatewayConfigStore>();
                var version = await store.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                if (version != state.Current.Version)
                {
                    await ReloadAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fail-static: keep the last-good snapshot; the next tick retries.
                logger.LogWarning(ex, "Config reconcile poll failed; keeping the last-good snapshot.");
            }
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TryLoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>Loads and swaps in a snapshot; returns false (keeping last-good) if the load threw.</summary>
    private async Task<bool> TryLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IGatewayConfigStore>();
            var snapshot = await store.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);

            state.Set(snapshot);
            if (!HasLoadedOnce)
            {
                HasLoadedOnce = true;
                logger.LogInformation("Loaded configuration snapshot from the database (version {Version}).", snapshot.Version);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to load configuration snapshot from the database; keeping the last-good snapshot.");
            return false;
        }
    }
}
