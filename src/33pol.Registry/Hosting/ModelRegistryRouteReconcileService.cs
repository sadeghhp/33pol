using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Registry.Services;

namespace Pol33.Registry.Hosting;

/// <summary>
/// Keeps this process's in-memory registry in step with the persisted route table by polling the
/// route version and reloading when it moves.
/// </summary>
/// <remarks>
/// The registry is a per-process cache of a shared table. Without this poll, a route added or deleted
/// on one replica stayed invisible to the others until they were restarted: the admin UI showed a
/// model that had been deleted elsewhere, adding it back reported "already exists" from a replica
/// whose memory still held it, and requests routed to models that no longer existed. Mirrors the
/// config-snapshot reconcile loop, and is fail-static in the same way — a failed poll keeps the
/// last-good registry and retries on the next tick.
/// </remarks>
internal sealed class ModelRegistryRouteReconcileService(
    IServiceScopeFactory scopeFactory,
    ModelRegistryLoader loader,
    ModelRegistryService registry,
    RegistryGate gate,
    IOptions<GatewayOptions> options,
    ILogger<ModelRegistryRouteReconcileService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = Math.Max(1, options.Value.ConfigReloadIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Model route reconcile poll failed; keeping the current registry.");
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        long version;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetService<IModelRouteRepository>();
            if (repository is null)
            {
                // DB-less deployment: models.json is the only source and nothing else can change it.
                return;
            }

            version = await repository.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (version == registry.AppliedRouteVersion && registry.IsLoaded)
        {
            return;
        }

        // Taken so a reload cannot interleave with a write and re-apply the pre-write set.
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (version == registry.AppliedRouteVersion && registry.IsLoaded)
            {
                return;
            }

            var previous = registry.AppliedRouteVersion;
            var count = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Model routes changed elsewhere (version {Previous} -> {Version}); reloaded {ModelCount} route(s).",
                previous,
                version,
                count);
        }
        finally
        {
            gate.Release();
        }
    }
}
