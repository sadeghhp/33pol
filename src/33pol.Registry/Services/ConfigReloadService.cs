using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;
using Pol33.Registry.Hosting;

namespace Pol33.Registry.Services;

public sealed class ConfigReloadService : BackgroundService, IConfigReload
{
    private readonly IModelRegistry _registry;
    private readonly GatewayOptions _options;
    private readonly ILogger<ConfigReloadService> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private byte[]? _lastFileHash;
    private DateTimeOffset? _lastReloadUtc;

    public ConfigReloadService(
        IModelRegistry registry,
        IOptions<GatewayOptions> options,
        ILogger<ConfigReloadService> logger)
    {
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsReloadInProgress => _reloadLock.CurrentCount == 0;

    public async Task<ConfigReloadResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!await _reloadLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return ConfigReloadResult.Error("Reload already in progress", suggestedStatusCode: 409);
        }

        try
        {
            var result = await ReloadFromDiskAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status == "success")
            {
                await RefreshFileHashAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public ConfigStatusResponse GetStatus()
    {
        var models = _registry.GetAllModels();
        return new ConfigStatusResponse
        {
            HotReloadEnabled = true,
            LastReload = _lastReloadUtc,
            ModelCount = models.Count,
            Models = models.Select(m => new ConfigStatusModel
            {
                Id = m.Id,
                Url = m.Url,
                Aliases = m.Aliases,
            }).ToList(),
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshFileHashAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.ConfigReloadIntervalSeconds));

        do
        {
            await PollForChangesAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task PollForChangesAsync(CancellationToken cancellationToken = default)
    {
        var configPath = ModelRegistryInitializer.ResolveConfigPath(_options.ModelsConfigPath);
        if (!File.Exists(configPath))
        {
            return;
        }

        var hash = await ComputeFileHashAsync(configPath, cancellationToken).ConfigureAwait(false);
        if (_lastFileHash is not null && hash.AsSpan().SequenceEqual(_lastFileHash))
        {
            return;
        }

        if (!await _reloadLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var result = await ReloadFromDiskAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status == "success")
            {
                _lastFileHash = hash;
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public async Task<ConfigReloadResult> ReloadFromDiskAsync(CancellationToken cancellationToken = default)
    {
        var configPath = ModelRegistryInitializer.ResolveConfigPath(_options.ModelsConfigPath);

        if (!File.Exists(configPath))
        {
            return ConfigReloadResult.Error($"Failed to reload: configuration file not found at '{configPath}'");
        }

        var previousCount = _registry.GetAllModels().Count;

        try
        {
            await _registry.LoadModelsAsync(configPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot reload failed for {ConfigPath}", configPath);
            return ConfigReloadResult.Error($"Failed to reload: {ex.Message}");
        }

        var models = _registry.GetAllModels();
        _lastReloadUtc = DateTimeOffset.UtcNow;

        return ConfigReloadResult.Success(
            "Configuration reloaded successfully",
            previousCount,
            models.Count,
            models.Select(m => m.Id).ToList());
    }

    public async Task RefreshFileHashAsync(CancellationToken cancellationToken = default)
    {
        var configPath = ModelRegistryInitializer.ResolveConfigPath(_options.ModelsConfigPath);
        if (!File.Exists(configPath))
        {
            _lastFileHash = null;
            return;
        }

        _lastFileHash = await ComputeFileHashAsync(configPath, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }
}
