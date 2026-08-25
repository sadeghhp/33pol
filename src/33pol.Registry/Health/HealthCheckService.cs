using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Diagnostics;
using Pol33.Core.Configuration;
using Pol33.Core.Models;

namespace Pol33.Registry.Health;

public sealed class HealthCheckService : BackgroundService
{
    /// <summary>
    /// Probed in order. <c>/v1/models</c> first because it is the surface the gateway actually
    /// forwards to — a backend that answers it is a backend that can serve inference.
    /// </summary>
    /// <remarks>
    /// The fallbacks exist for runtimes that do not implement the models endpoint. <c>/</c> is
    /// deliberately last and deliberately weak: many things answer 200 on the site root without the
    /// model server being up, so it is a last resort rather than the primary signal it used to be.
    /// </remarks>
    private static readonly string[] ProbePaths = ["/v1/models", "/health", "/api/tags", "/"];

    /// <summary>Bounds fan-out so a large registry does not open one connection per model at once.</summary>
    private const int MaxConcurrentProbes = 8;

    private readonly IModelRegistry _registry;
    private readonly IBackendHealthStore _healthStore;
    private readonly IUpstreamBearerTokenResolver _bearerTokenResolver;
    private readonly GatewayOptions _options;
    private readonly ILogger<HealthCheckService> _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IGatewayErrorRecorder? _errorRecorder;

    // This service's own memory of each backend's last verdict. Transition detection cannot lean
    // on the store: a stub store (or one that was just pruned) answers "unknown" every sweep, which
    // would turn a standing outage into one error record per interval.
    private readonly ConcurrentDictionary<string, bool> _lastVerdict = new(StringComparer.OrdinalIgnoreCase);

    public HealthCheckService(
        IModelRegistry registry,
        IBackendHealthStore healthStore,
        IUpstreamBearerTokenResolver bearerTokenResolver,
        IOptions<GatewayOptions> options,
        ILogger<HealthCheckService> logger,
        HttpClient? httpClient = null,
        IGatewayErrorRecorder? errorRecorder = null)
    {
        _registry = registry;
        _healthStore = healthStore;
        _bearerTokenResolver = bearerTokenResolver;
        _options = options.Value;
        _logger = logger;
        _errorRecorder = errorRecorder;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        _ownsHttpClient = httpClient is null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HealthCheckIntervalSeconds));

        do
        {
            try
            {
                await CheckAllBackendsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let one bad model (a malformed URL, say) escape: an unhandled exception here
                // takes the whole host down under the default BackgroundService behaviour, and
                // freezes every backend's health state on the way out.
                _logger.LogError(ex, "Backend health sweep failed; will retry on the next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task CheckAllBackendsAsync(CancellationToken cancellationToken = default)
    {
        var models = _registry.GetAllModels();

        // Removed or renamed models must not linger with their last status: prune first so a sweep
        // over an emptied registry also clears the store.
        PruneRemovedModels(models);
        if (models.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(MaxConcurrentProbes, MaxConcurrentProbes);
        var tasks = models.Select(async model =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CheckBackendAsync(model, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void PruneRemovedModels(IReadOnlyList<ModelConfig> models)
    {
        // IBackendHealthStore has no prune operation; only the real store accumulates entries, so
        // fakes and always-healthy stubs are left alone.
        if (_healthStore is BackendHealthStore store)
        {
            store.RetainOnly(models.Select(m => m.Id));
        }
    }

    public async Task CheckBackendAsync(ModelConfig model, CancellationToken cancellationToken = default)
    {
        // Probes carry the model's own upstream credential. Without it an authenticated upstream
        // answers 401 to every probe path, so the backend was permanently marked unhealthy and every
        // inference request to it returned 502 — a total outage for any cloud provider.
        var bearerToken = ResolveBearerTokenSafely(model);

        var (isHealthy, statusCode, error) = await ProbeBackendAsync(model.Url, bearerToken, cancellationToken)
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
            var detail = error ?? statusCode?.ToString() ?? "unknown";
            _logger.LogWarning(
                "Backend {ModelId} at {Url} is unhealthy: {Error}",
                model.Id,
                model.Url,
                detail);

            // One record per transition, not per sweep: an outage is one fault however many
            // probes observe it, and a repeat every interval would bury everything else. The
            // attention item shows the live state; this is the durable history of it.
            var wasHealthy = !_lastVerdict.TryGetValue(model.Id, out var last) || last;
            if (wasHealthy)
            {
                _errorRecorder?.Record(new GatewayErrorRecord
                {
                    Id = $"err_{Guid.NewGuid():N}",
                    Fingerprint = string.Empty,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Level = GatewayLogLevel.Error.ToString(),
                    Source = GatewayErrorSourceNames.Health,
                    Category = nameof(HealthCheckService),
                    EventCode = "backend_unhealthy",
                    Message = $"Backend for model '{model.Id}' became unhealthy: {detail}",
                    StatusCode = statusCode ?? 0,
                    ModelId = model.Id,
                    UpstreamTarget = model.Url,
                    Outcome = "backend_unhealthy",
                    Hint = GatewayLogHints.ForUpstreamStatus(statusCode ?? 0, model.Url, null, model.Id),
                });
            }
        }

        _lastVerdict[model.Id] = isHealthy;
    }

    private string? ResolveBearerTokenSafely(ModelConfig model)
    {
        try
        {
            return _bearerTokenResolver.ResolveBearerToken(model.UpstreamAuth);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not resolve upstream credential for model {ModelId}; probing without it", model.Id);
            return null;
        }
    }

    public Task<(bool IsHealthy, int? StatusCode, string? Error)> ProbeBackendAsync(
        string baseUrl,
        CancellationToken cancellationToken = default) =>
        ProbeBackendAsync(baseUrl, bearerToken: null, cancellationToken);

    /// <summary>
    /// Probes a backend, returning the most informative outcome observed across the probe paths.
    /// </summary>
    /// <remarks>
    /// A 401/403 counts as <em>healthy</em>: the backend answered, so it is reachable and serving.
    /// A credential problem is a configuration fault that belongs in the model's upstream auth
    /// settings, not a reason to take the model out of rotation — and reporting it as ill health
    /// produced an opaque 502 that pointed at the wrong thing entirely.
    ///
    /// The real status code and error are retained rather than collapsed into a fixed string; they
    /// are what the admin backends view shows an operator trying to work out why a model is down.
    /// </remarks>
    public async Task<(bool IsHealthy, int? StatusCode, string? Error)> ProbeBackendAsync(
        string baseUrl,
        string? bearerToken,
        CancellationToken cancellationToken = default)
    {
        int? lastStatusCode = null;
        string? lastError = null;

        foreach (var path in ProbePaths)
        {
            Uri requestUri;
            try
            {
                requestUri = BuildProbeUri(baseUrl, path);
            }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException)
            {
                return (false, null, $"Invalid backend URL '{baseUrl}': {ex.Message}");
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                if (!string.IsNullOrWhiteSpace(bearerToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return (true, status, null);
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return (true, status, $"Backend reachable but rejected the gateway's credential (HTTP {status}). "
                                          + "Check this model's upstream auth configuration.");
                }

                lastStatusCode = status;
                lastError = $"{path} returned HTTP {status}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                lastError = $"{path}: {ex.Message}";
            }
        }

        return (false, lastStatusCode, lastError ?? "All probe endpoints failed");
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
