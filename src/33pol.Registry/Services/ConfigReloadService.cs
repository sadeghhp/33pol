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
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly ModelRegistryService _registry;
    private readonly GatewayOptions _options;
    private readonly RegistryGate _gate;
    private readonly ILogger<ConfigReloadService> _logger;
    private readonly bool _watchEnabled;
    private readonly string _configPath;

    private byte[]? _lastFileHash;
    private DateTimeOffset? _lastReloadUtc;
    private CancellationTokenSource? _debounceCts;
    private readonly object _debounceLock = new();

    public ConfigReloadService(
        ModelRegistryService registry,
        IOptions<GatewayOptions> options,
        RegistryGate gate,
        IHostEnvironment hostEnvironment,
        ILogger<ConfigReloadService> logger)
    {
        _registry = registry;
        _options = options.Value;
        _gate = gate;
        _logger = logger;
        _watchEnabled = _options.RegistryWatchEnabled ?? hostEnvironment.IsDevelopment();
        _configPath = ModelRegistryInitializer.ResolveConfigPath(_options.ModelsConfigPath);
    }

    public bool IsReloadInProgress => _gate.IsHeld;

    public bool WatchEnabled => _watchEnabled;

    public async Task<ConfigReloadResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.TryEnterAsync(cancellationToken).ConfigureAwait(false))
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
            WatchEnabled = _watchEnabled,
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

        if (_watchEnabled)
        {
            await RunFileWatcherAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        var interval = Math.Clamp(_options.ConfigReloadIntervalSeconds, 1, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));

        do
        {
            await PollForChangesAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task PollForChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configPath))
        {
            return;
        }

        var hash = await ComputeFileHashAsync(_configPath, cancellationToken).ConfigureAwait(false);
        if (_lastFileHash is not null && hash.AsSpan().SequenceEqual(_lastFileHash))
        {
            return;
        }

        if (!await _gate.TryEnterAsync(cancellationToken).ConfigureAwait(false))
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
        if (!File.Exists(_configPath))
        {
            return ConfigReloadResult.Error($"Failed to reload: configuration file not found at '{_configPath}'");
        }

        var previousCount = _registry.GetAllModels().Count;

        try
        {
            await _registry.LoadModelsAsync(_configPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hot reload failed for {ConfigPath}", _configPath);
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
        if (!File.Exists(_configPath))
        {
            _lastFileHash = null;
            return;
        }

        _lastFileHash = await ComputeFileHashAsync(_configPath, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ComputeFileHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunFileWatcherAsync(CancellationToken stoppingToken)
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _logger.LogWarning(
                "Registry watch enabled but directory {Directory} is missing; falling back to poll.",
                directory);
            await RunPollFallbackAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        var fileName = Path.GetFileName(_configPath);
        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        void OnChange(object _, FileSystemEventArgs __) => ScheduleDebouncedReload();
        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Renamed += (_, _) => ScheduleDebouncedReload();

        _logger.LogInformation(
            "Registry file watch enabled for {ConfigPath} (debounce {DebounceMs} ms).",
            _configPath,
            DebounceDelay.TotalMilliseconds);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            watcher.Changed -= OnChange;
            watcher.Created -= OnChange;
        }
    }

    private async Task RunPollFallbackAsync(CancellationToken stoppingToken)
    {
        var interval = Math.Clamp(_options.ConfigReloadIntervalSeconds, 1, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));

        do
        {
            await PollForChangesAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void ScheduleDebouncedReload()
    {
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = DebouncedReloadAsync(token);
        }
    }

    private async Task DebouncedReloadAsync(CancellationToken debounceToken)
    {
        try
        {
            await Task.Delay(DebounceDelay, debounceToken).ConfigureAwait(false);
            await PollForChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (debounceToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Debounced registry reload failed.");
        }
    }
}
