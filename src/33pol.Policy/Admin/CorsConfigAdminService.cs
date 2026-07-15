using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Policy.Admin;

public sealed class CorsConfigAdminService : ICorsConfigAdminService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IOptionsMonitor<GatewayOptions> _optionsMonitor;
    private readonly ILogger<CorsConfigAdminService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public CorsConfigAdminService(
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptionsMonitor<GatewayOptions> optionsMonitor,
        ILogger<CorsConfigAdminService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public string[] GetCurrent() => _optionsMonitor.CurrentValue.Cors.GetNormalizedOrigins();

    public async Task<CorsConfigUpdateResult> UpdateAsync(
        IReadOnlyList<string> allowedOrigins,
        CancellationToken cancellationToken = default)
    {
        if (!CorsConfigValidation.TryValidate(allowedOrigins, out var validationError, out var normalized))
        {
            return CorsConfigUpdateResult.Fail(validationError!, statusCode: 400);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ResolveAppSettingsPath();
            await AppSettingsCorsPersistence
                .WriteAsync(path, normalized, cancellationToken)
                .ConfigureAwait(false);

            if (_configuration is IConfigurationRoot configurationRoot)
            {
                configurationRoot.Reload();
            }
            else
            {
                _logger.LogWarning(
                    "Configuration is not reloadable; CORS origins were persisted but in-memory options may be stale until restart.");
            }

            _ = _optionsMonitor.CurrentValue;

            _logger.LogInformation("Updated CORS allowed origins in {AppSettingsPath}.", path);
            return CorsConfigUpdateResult.Ok("CORS allowed origins updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist CORS configuration.");
            return CorsConfigUpdateResult.Fail("Failed to persist CORS configuration.", statusCode: 500);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private string ResolveAppSettingsPath()
    {
        var configured = _configuration["Gateway:AppSettingsPath"];
        var fileName = string.IsNullOrWhiteSpace(configured) ? "appsettings.json" : configured.Trim();
        return Path.IsPathRooted(fileName)
            ? fileName
            : Path.Combine(_environment.ContentRootPath, fileName);
    }
}
