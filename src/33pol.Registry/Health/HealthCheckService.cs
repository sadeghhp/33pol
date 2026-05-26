using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Registry.Health;

public sealed class HealthCheckService : BackgroundService
{
    private static readonly string[] ProbePaths = ["/health", "/api/tags", "/"];

    private readonly IModelRegistry _registry;
    private readonly IBackendHealthStore _healthStore;
    private readonly GatewayOptions _options;
    private readonly ILogger<HealthCheckService> _logger;
    private readonly HttpClient _httpClient;

    public HealthCheckService(
        IModelRegistry registry,
        IBackendHealthStore healthStore,
        IOptions<GatewayOptions> options,
        ILogger<HealthCheckService> logger,
        HttpClient? httpClient = null)
    {
        _registry = registry;
        _healthStore = healthStore;
        _options = options.Value;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsHttpClient = httpClient is null;
    }

    private readonly bool _ownsHttpClient;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds));

        do
        {
            await CheckAllBackendsAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task CheckAllBackendsAsync(CancellationToken cancellationToken = default)
    {
        var models = _registry.GetAllModels();
        if (models.Count == 0)
        {
            return;
        }

        var tasks = models.Select(model => CheckBackendAsync(model, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task CheckBackendAsync(ModelConfig model, CancellationToken cancellationToken = default)
    {
        var (isHealthy, statusCode, error) = await ProbeBackendAsync(model.Url, cancellationToken)
            .ConfigureAwait(false);

        _healthStore.SetHealth(new BackendHealth(
            model.Id,
            model.Url,
            isHealthy,
            statusCode,
            error,
            DateTimeOffset.UtcNow));

        if (!isHealthy)
        {
            _logger.LogWarning(
                "Backend {ModelId} at {Url} is unhealthy: {Error}",
                model.Id,
                model.Url,
                error ?? statusCode?.ToString() ?? "unknown");
        }
    }

    public async Task<(bool IsHealthy, int? StatusCode, string? Error)> ProbeBackendAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        foreach (var path in ProbePaths)
        {
            try
            {
                var requestUri = BuildProbeUri(baseUrl, path);
                using var response = await _httpClient.GetAsync(requestUri, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return (true, (int)response.StatusCode, null);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Try next probe path.
            }
        }

        return (false, null, "All probe endpoints failed");
    }

    public static Uri BuildProbeUri(string baseUrl, string path)
    {
        var normalizedBase = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var trimmedPath = path.TrimStart('/');
        return new Uri(new Uri(normalizedBase, UriKind.Absolute), trimmedPath);
    }

    public override void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        base.Dispose();
    }
}
