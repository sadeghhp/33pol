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
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(500);

    private readonly IModelRegistry _registry;
    private readonly RegistryGate _gate;
    private readonly GatewayOptions _options;
    private readonly ILogger<ConfigReloadService> _logger;

    private byte[]? _lastFileHash;
    private DateTimeOffset? _lastReloadUtc;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private int _debounceScheduled;

    public ConfigReloadService(
        IModelRegistry registry,
        RegistryGate gate,
        IOptions<GatewayOptions> options,
        ILogger<ConfigReloadService> logger)
    {
        _registry = registry;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsReloadInProgress => _gate.IsHeld;

    public async Task<ConfigReloadResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_gate.TryEnter())
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
            _gate.Release();
        }
    }

    public ConfigStatusResponse GetStatus()
    {
        var models = _registry.GetAllModels();
        return new ConfigStatusResponse
        {
            HotReloadEnabled = true,
            WatchEnabled = _options.RegistryWatchEnabled,
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

        if (_options.RegistryWatchEnabled)
        {
            StartFileWatcher();
            try
            {
                // FileSystemWatcher is best-effort and can miss events under heavy I/O or editor save patterns.
                // Keep a low-frequency hash poll as a safety net so hot reload remains reliable.
                var fallbackIntervalSeconds = Math.Clamp(_options.ConfigReloadIntervalSeconds, 1, 300);
                var fallbackInterval = TimeSpan.FromSeconds(Math.Min(fallbackIntervalSeconds, 5));
                using var timer = new PeriodicTimer(fallbackInterval);

                do
                {
                    await PollForChangesAsync(stoppingToken).ConfigureAwait(false);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // expected on shutdown
            }
            finally
            {
                StopFileWatcher();
            }

            return;
        }

        using var pollTimer = new PeriodicTimer(TimeSpan.FromSeconds(_options.ConfigReloadIntervalSeconds));

        do
        {
            await PollForChangesAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await pollTimer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
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

        if (!_gate.TryEnter())
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
            _gate.Release();
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

    internal void ScheduleDebouncedReload()
    {
        if (Interlocked.Exchange(ref _debounceScheduled, 1) == 1)
        {
            return;
        }

        _debounceTimer ??= new Timer(
            static state => ((ConfigReloadService)state!).OnDebounceElapsed(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);

        _debounceTimer.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
    }

    private async void OnDebounceElapsed()
    {
        Interlocked.Exchange(ref _debounceScheduled, 0);

        try
        {
            await PollForChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Debounced registry reload failed.");
        }
    }

    private void StartFileWatcher()
    {
        var configPath = ModelRegistryInitializer.ResolveConfigPath(_options.ModelsConfigPath);
        var directory = Path.GetDirectoryName(configPath);
        var fileName = Path.GetFileName(configPath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            _logger.LogWarning(
                "Registry watch disabled: cannot watch invalid path {ConfigPath}.",
                configPath);
            return;
        }

        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        FileSystemEventHandler handler = (_, _) => ScheduleDebouncedReload();
        _watcher.Changed += handler;
        _watcher.Created += handler;
        _watcher.Renamed += (_, _) => ScheduleDebouncedReload();

        _logger.LogInformation(
            "Registry file watch enabled for {ConfigPath} (debounce {DebounceMs} ms).",
            configPath,
            WatchDebounce.TotalMilliseconds);
    }

    private void StopFileWatcher()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = null;

        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }
}
