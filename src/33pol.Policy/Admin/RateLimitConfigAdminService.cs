using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;

namespace Pol33.Policy.Admin;

public sealed class RateLimitConfigAdminService : IRateLimitConfigAdminService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IOptionsMonitor<RateLimitingOptions> _optionsMonitor;
    private readonly ILogger<RateLimitConfigAdminService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public RateLimitConfigAdminService(
        IConfiguration configuration,
        IHostEnvironment environment,
        IOptionsMonitor<RateLimitingOptions> optionsMonitor,
        ILogger<RateLimitConfigAdminService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    public RateLimitAdminConfig GetCurrent()
    {
        var options = _optionsMonitor.CurrentValue;
        return new RateLimitAdminConfig
        {
            Default = CloneTier(options.Default),
            Plans = options.Plans.ToDictionary(
                static p => p.Key,
                static p => CloneTier(p.Value),
                StringComparer.OrdinalIgnoreCase),
        };
    }

    public async Task<RateLimitConfigUpdateResult> UpdateAsync(
        RateLimitTierOptions defaultTier,
        IReadOnlyDictionary<string, RateLimitTierOptions> plans,
        CancellationToken cancellationToken = default)
    {
        if (!RateLimitConfigValidation.TryValidate(defaultTier, plans, out var validationError))
        {
            return RateLimitConfigUpdateResult.Fail(validationError!, statusCode: 400);
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ResolveAppSettingsPath();
            await AppSettingsRateLimitPersistence
                .WriteAsync(path, defaultTier, plans, cancellationToken)
                .ConfigureAwait(false);

            if (_configuration is IConfigurationRoot configurationRoot)
            {
                configurationRoot.Reload();
            }
            else
            {
                _logger.LogWarning(
                    "Configuration is not reloadable; rate limits were persisted but in-memory options may be stale until restart.");
            }

            _ = _optionsMonitor.CurrentValue;

            _logger.LogInformation("Updated rate limits in {AppSettingsPath}.", path);
            return RateLimitConfigUpdateResult.Ok("Rate limits updated.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist rate limit configuration.");
            return RateLimitConfigUpdateResult.Fail("Failed to persist rate limit configuration.", statusCode: 500);
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

    private static RateLimitTierOptions CloneTier(RateLimitTierOptions tier) =>
        new()
        {
            Rpm = tier.Rpm,
            Burst = tier.Burst,
            MaxConcurrentStreams = tier.MaxConcurrentStreams,
        };
}
