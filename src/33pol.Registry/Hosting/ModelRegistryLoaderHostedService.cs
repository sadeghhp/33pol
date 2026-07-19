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
            logger.LogWarning(ex, "Failed to load model routes at startup; registry is empty until a reload.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
