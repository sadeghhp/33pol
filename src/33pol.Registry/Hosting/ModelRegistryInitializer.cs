using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Registry.Hosting;

public sealed class ModelRegistryInitializer : IHostedService
{
    private readonly IModelRegistry _registry;
    private readonly GatewayOptions _options;
    private readonly ILogger<ModelRegistryInitializer> _logger;

    public ModelRegistryInitializer(
        IModelRegistry registry,
        IOptions<GatewayOptions> options,
        ILogger<ModelRegistryInitializer> logger)
    {
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configPath = ResolveConfigPath(_options.ModelsConfigPath);

        if (!File.Exists(configPath))
        {
            _logger.LogWarning(
                "Models configuration file not found at {ConfigPath}; registry remains empty until reload.",
                configPath);
            return;
        }

        await _registry.LoadModelsAsync(configPath, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static string ResolveConfigPath(string modelsConfigPath)
    {
        var combined = Path.Combine(AppContext.BaseDirectory, modelsConfigPath);
        return File.Exists(combined) ? combined : modelsConfigPath;
    }
}
