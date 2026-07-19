using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Configuration;
using Pol33.Core.Identity;
using Pol33.Core.Models;
using Pol33.Persistence.Entities;
using Pol33.Persistence.Mapping;
using Pol33.Persistence.Security;

namespace Pol33.Persistence.Bootstrap;

public sealed class GatewayDbBootstrap
{
    private readonly GatewayDbContext _db;
    private readonly GatewayBootstrapOptions _options;
    private readonly GatewayOptions _gatewayOptions;
    private readonly RateLimitingOptions _rateLimitingOptions;
    private readonly QuotaOptions _quotaOptions;
    private readonly ILogger<GatewayDbBootstrap> _logger;

    public GatewayDbBootstrap(
        GatewayDbContext db,
        IOptions<GatewayBootstrapOptions> options,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<RateLimitingOptions> rateLimitingOptions,
        IOptions<QuotaOptions> quotaOptions,
        ILogger<GatewayDbBootstrap> logger)
    {
        _db = db;
        _options = options.Value;
        _gatewayOptions = gatewayOptions.Value;
        _rateLimitingOptions = rateLimitingOptions.Value;
        _quotaOptions = quotaOptions.Value;
        _logger = logger;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_db.Database.IsRelational())
        {
            await _db.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            await _db.Database.EnsureCreatedAsync(cancellationToken);
        }

        // Seed database-backed config from appsettings once (idempotent), independent of whether the
        // default tenant/admin-key bootstrap is enabled.
        await SeedCorsSettingsAsync(cancellationToken);
        await SeedRateLimitSettingsAsync(cancellationToken);
        await SeedModelRoutesAsync(cancellationToken);
        await SeedQuotaSettingsAsync(cancellationToken);

        if (!_options.Enabled)
        {
            _logger.LogInformation("Gateway database bootstrap is disabled");
            return;
        }

        if (await _db.Tenants.AnyAsync(cancellationToken))
        {
            return;
        }

        var adminKey = _options.AdminApiKey;
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            _logger.LogWarning(
                "Gateway database is empty and {Setting} is not set; skipping bootstrap tenant creation",
                $"{GatewayBootstrapOptions.SectionName}:AdminApiKey");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        var prefix = ApiKeyHashing.CreatePrefix(adminKey);
        var hash = ApiKeyHashing.Hash(adminKey, _options.KeyPepper);

        _db.Tenants.Add(new TenantEntity
        {
            Id = tenantId,
            Slug = _options.TenantSlug,
            Name = _options.TenantName,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _db.ApiKeys.Add(new ApiKeyEntity
        {
            Id = keyId,
            TenantId = tenantId,
            KeyHash = hash,
            KeyPrefix = prefix,
            Role = ApiKeyRole.Admin,
            Scopes = ["admin"],
            CreatedAt = now,
        });

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Bootstrapped tenant {TenantSlug} with admin API key prefix {KeyPrefix}",
            _options.TenantSlug,
            prefix);
    }

    private async Task SeedCorsSettingsAsync(CancellationToken cancellationToken)
    {
        if (await _db.CorsSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        var origins = _gatewayOptions.Cors.GetNormalizedOrigins();
        _db.CorsSettings.Add(new CorsSettingsEntity
        {
            Id = 1,
            AllowedOrigins = [.. origins],
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Seeded CORS settings with {OriginCount} allowed origin(s) from configuration.", origins.Length);
    }

    private async Task SeedRateLimitSettingsAsync(CancellationToken cancellationToken)
    {
        if (await _db.RateLimitDefaults.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var defaults = _rateLimitingOptions.Default;
        _db.RateLimitDefaults.Add(new RateLimitDefaultsEntity
        {
            Id = 1,
            Rpm = defaults.Rpm,
            Burst = defaults.Burst,
            MaxConcurrentStreams = defaults.MaxConcurrentStreams,
            UpdatedAt = now,
        });

        foreach (var (slug, tier) in _rateLimitingOptions.Plans)
        {
            _db.RateLimitPlans.Add(new RateLimitPlanEntity
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Rpm = tier.Rpm,
                Burst = tier.Burst,
                MaxConcurrentStreams = tier.MaxConcurrentStreams,
                UpdatedAt = now,
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Seeded rate-limit settings (default + {PlanCount} plan tier(s)) from configuration.",
            _rateLimitingOptions.Plans.Count);
    }

    private async Task SeedQuotaSettingsAsync(CancellationToken cancellationToken)
    {
        if (await _db.QuotaSettings.AnyAsync(cancellationToken))
        {
            return;
        }

        _db.QuotaSettings.Add(new QuotaSettingsEntity
        {
            Id = 1,
            DefaultMonthlyTokenLimit = _quotaOptions.DefaultMonthlyTokenLimit,
            SoftLimitRatio = (decimal)_quotaOptions.SoftLimitRatio,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "Seeded quota settings (limit {Limit}, soft ratio {Ratio}) from configuration.",
            _quotaOptions.DefaultMonthlyTokenLimit,
            _quotaOptions.SoftLimitRatio);
    }

    private static readonly JsonSerializerOptions ModelsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private async Task SeedModelRoutesAsync(CancellationToken cancellationToken)
    {
        if (await _db.ModelRoutes.AnyAsync(cancellationToken))
        {
            return;
        }

        var path = ResolveModelsConfigPath(_gatewayOptions.ModelsConfigPath);
        if (!File.Exists(path))
        {
            _logger.LogWarning(
                "Models configuration file not found at {ConfigPath}; model_routes left empty until an admin write.",
                path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var config = JsonSerializer.Deserialize<ModelRegistryConfig>(json, ModelsJsonOptions);
        if (config?.Models is null || config.Models.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var model in config.Models)
        {
            _db.ModelRoutes.Add(ModelRouteEntityMapper.ToEntity(model, now));
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Seeded {ModelCount} model route(s) from {ConfigPath}.", config.Models.Count, path);
    }

    private static string ResolveModelsConfigPath(string modelsConfigPath)
    {
        var combined = Path.Combine(AppContext.BaseDirectory, modelsConfigPath);
        return File.Exists(combined) ? combined : modelsConfigPath;
    }
}
