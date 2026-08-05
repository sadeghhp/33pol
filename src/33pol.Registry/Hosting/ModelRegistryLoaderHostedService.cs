using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pol33.Registry.Services;

namespace Pol33.Registry.Hosting;

/// <summary>
/// Loads the model registry from its source (database or file) at startup. Registered after the
/// persistence bootstrap so migrations and the model-route seed run first. Degrades rather than
/// crashing: a load failure leaves the registry empty and is logged.
/// </summary>
internal sealed class ModelRegistryLoaderHostedService(
    ModelRegistryLoader loader,
    ILogger<ModelRegistryLoaderHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var count = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Loaded {ModelCount} model route(s) into the registry.", count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Loud on purpose: with no routes loaded the gateway answers every inference request with
            // "model not found" while looking healthy, so this must not read as a routine warning.
            // Readiness reports not-ready off the registry's load state until a reload succeeds.
            logger.LogError(
                ex,
                "Failed to load model routes at startup; the gateway has NO routes and will reject all "
                + "inference requests until a reload succeeds.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
